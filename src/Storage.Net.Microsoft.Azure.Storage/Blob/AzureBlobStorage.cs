﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Storage.Net.Blob;
using Microsoft.WindowsAzure.Storage.Blob;
using AzureStorageException = Microsoft.WindowsAzure.Storage.StorageException;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using System.Threading.Tasks;
using System.Threading;

namespace Storage.Net.Microsoft.Azure.Storage.Blob
{
   /// <summary>
   /// Azure Blob Storage
   /// </summary>
   public class AzureBlobStorage : AsyncBlobStorage
   {
      private readonly CloudBlobContainer _blobContainer;

      /// <summary>
      /// Creates an instance from account name, private key and container name
      /// </summary>
      public AzureBlobStorage(string accountName, string key, string containerName)
      {
         ValidateContainerName(containerName);

         var account = new CloudStorageAccount(
            new StorageCredentials(accountName, key),
            true);

         CloudBlobClient blobClient = account.CreateCloudBlobClient();

         _blobContainer = blobClient.GetContainerReference(containerName);
         _blobContainer.CreateIfNotExistsAsync().Wait();
      }

      /// <summary>
      /// Returns reference to the native Azure SDK blob container
      /// </summary>
      public CloudBlobContainer NativeBlobContainer => _blobContainer;

      /// <summary>
      /// Get native Azure blob absolute URI by blob ID
      /// </summary>
      /// <param name="id">Blob ID</param>
      /// <returns>URI of the blob or null if blob doesn't exist</returns>
      public async Task<Uri> GetNativeBlobUriAsync(string id)
      {
         GenericValidation.CheckBlobId(id);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);

         bool exists = await blob.ExistsAsync();
         if (!exists) return null;

         return blob.Uri;
      }

      /// <summary>
      /// Gets native Azure Blob absolute URI by blob ID and shared access policy
      /// </summary>
      /// <param name="id">Blob ID</param>
      /// <param name="policy">SAS policy</param>
      /// <returns>Blob URI with SAS policy, or null if blob doesn't exist</returns>
      public async Task<Uri> GetNativeSasUri(string id, SharedAccessBlobPolicy policy)
      {
         GenericValidation.CheckBlobId(id);
         if (policy == null) throw new ArgumentNullException(nameof(policy));

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);

         bool exists = await blob.ExistsAsync();
         if (!exists) return null;

         string sas = blob.GetSharedAccessSignature(policy);

         return new Uri(blob.Uri.ToString() + sas);
      }

      /// <summary>
      /// Creates and instance from network credential and container name
      /// </summary>
      /// <param name="credential"></param>
      /// <param name="containerName"></param>
      public AzureBlobStorage(NetworkCredential credential, string containerName) :
         this(credential.UserName, credential.Password, containerName)
      {

      }

      /// <summary>
      /// Creates an insance from connection string
      /// </summary>
      public AzureBlobStorage(string connectionString, string containerName)
      {
         if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
         ValidateContainerName(containerName);

         if (!CloudStorageAccount.TryParse(connectionString, out CloudStorageAccount account))
         {
            throw new ArgumentException("could not parse provided connection string");
         }

         CloudBlobClient blobClient = account.CreateCloudBlobClient();

         _blobContainer = blobClient.GetContainerReference(containerName);
         _blobContainer.CreateIfNotExistsAsync().Wait();
      }

      private void ValidateContainerName(string containerName)
      {
         if (containerName == null) throw new ArgumentNullException(nameof(containerName));

         /* from MSDN:
          *
          A container name must be a valid DNS name, conforming to the following naming rules:
          1. Container names must start with a letter or number, and can contain only letters, numbers, and the dash (-) character.
          2. Every dash (-) character must be immediately preceded and followed by a letter or number; consecutive dashes are not permitted in container names.
          3. All letters in a container name must be lowercase.
          4. Container names must be from 3 through 63 characters long.
         */

         //1. todo

         //2. todo

         //3. check that all characters are lowercase
         for (int i = 0; i < containerName.Length; i++)
         {
            if (char.IsLetter(containerName[i]) && !char.IsLower(containerName, i))
            {
               throw new ArgumentOutOfRangeException(nameof(containerName),
                  $"container [{containerName}] has uppercase character at position {i}");
            }
         }

         //4. check for length
         if (containerName.Length < 3 || containerName.Length > 63)
         {
            throw new ArgumentOutOfRangeException(nameof(containerName),
               $"container [{containerName}] length must be between 3 and 63 but it's {containerName.Length}");
         }
      }

      /// <summary>
      /// Gets all the blob names, then filters by prefix optionally
      /// </summary>
      protected override async Task<IEnumerable<BlobId>> ListAsync(string[] folderPath, string prefix, bool recurse, CancellationToken cancellationToken)
      {
         return await ListFolder(
            StoragePath.Combine(folderPath),
            prefix,
            recurse,
            cancellationToken); 
      }

      private async Task<IEnumerable<BlobId>> ListFolder(string path, string prefix, bool recurse, CancellationToken cancellationToken)
      {
         //todo: need to start off CloudBlobDirectory.ListBlobs, but for this the directory need to be found first

         var result = new List<BlobId>();

         BlobContinuationToken token = null;

         //blob path cannot start with '/'
         string listPrefix = StoragePath.Combine(path, prefix).Trim(StoragePath.PathSeparator);
         if (prefix != null) listPrefix = StoragePath.Combine(listPrefix, prefix);

         do
         {
            BlobResultSegment segment = await _blobContainer.ListBlobsSegmentedAsync(
               listPrefix,
               false,
               BlobListingDetails.None,
               null,
               token,
               null,
               null,
               cancellationToken);

            foreach (IListBlobItem blob in segment.Results)
            {
               BlobId id;

               if (blob is CloudBlockBlob blockBlob)
                  id = new BlobId(blockBlob.Name, BlobItemKind.File);
               else if (blob is CloudAppendBlob appendBlob)
                  id = new BlobId(appendBlob.Name, BlobItemKind.File);
               else if (blob is CloudBlobDirectory dirBlob)
                  id = new BlobId(dirBlob.Prefix, BlobItemKind.Folder);
               else
                  throw new InvalidOperationException($"unknown item type {blob.GetType()}");

               result.Add(id);
            }

         }
         while (token != null);

         if(recurse)
         {
            List<BlobId> folderIds = result.Where(r => r.Kind == BlobItemKind.Folder).ToList();
            foreach(BlobId folderId in folderIds)
            {
               IEnumerable<BlobId> children = await ListFolder(
                  StoragePath.Combine(path, folderId.Id),
                  prefix,
                  recurse,
                  cancellationToken);

               result.AddRange(children);
            }
         }

         return result;
      }

      /// <summary>
      /// Deletes blob remotely
      /// </summary>
      public override async Task DeleteAsync(string id)
      {
         GenericValidation.CheckBlobId(id);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);
         await blob.DeleteIfExistsAsync();
      }

      /// <summary>
      /// Uploads from stream
      /// </summary>
      public override async Task WriteAsync(string id, Stream sourceStream)
      {
         GenericValidation.CheckBlobId(id);
         GenericValidation.CheckSourceStream(sourceStream);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(ToPath(id));
         await blob.UploadFromStreamAsync(sourceStream);
      }

      /// <summary>
      /// Appends to the append blob.
      /// </summary>
      public override async Task AppendAsync(string id, Stream sourceStream)
      {
         GenericValidation.CheckBlobId(id);
         GenericValidation.CheckSourceStream(sourceStream);

         CloudAppendBlob cab = _blobContainer.GetAppendBlobReference(id);
         if (!(await cab.ExistsAsync())) await cab.CreateOrReplaceAsync();

         await cab.AppendFromStreamAsync(sourceStream);
      }

      /// <summary>
      /// Opens stream to read
      /// </summary>
      /// <param name="id"></param>
      /// <returns></returns>
      public override async Task<Stream> OpenReadAsync(string id)
      {
         GenericValidation.CheckBlobId(id);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);

         try
         {
            return await blob.OpenReadAsync();
         }
         catch(AzureStorageException ex)
         {
            if (!TryHandleStorageException(ex)) throw;
         }

         throw new Exception("must not be here");
      }

      /// <summary>
      /// Checks if the blob exists by trying to fetch attributes from the blob reference and checkign if that fails
      /// </summary>
      public override async Task<bool> ExistsAsync(string id)
      {
         GenericValidation.CheckBlobId(id);
         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);
         return await blob.ExistsAsync();
      }

      /// <summary>
      /// Gets blob metadata
      /// </summary>
      public override async Task<BlobMeta> GetMetaAsync(string id)
      {
         GenericValidation.CheckBlobId(id);

         CloudBlob blob = _blobContainer.GetBlobReference(id);
         if (!(await blob.ExistsAsync())) return null;

         await blob.FetchAttributesAsync();

         //ContentMD5 is base64-encoded hash, whereas we work with HEX encoded ones
         string md5 = blob.Properties.ContentMD5.Base64DecodeAsBytes().ToHexString();

         return new BlobMeta(
            blob.Properties.Length,
            md5);
      }

      private static bool TryHandleStorageException(AzureStorageException ex)
      {
         if(ex.RequestInformation?.HttpStatusCode == 404)
         {
            throw new StorageException(ErrorCode.NotFound, ex);
         }

         return false;
      }

      private static string ToPath(string path)
      {
         if (path == null) return null;

         return path.Trim(StoragePath.PathSeparator);
      }
   }
}