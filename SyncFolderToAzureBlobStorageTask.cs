namespace RhysG.MSBuild.Azure
{
    using System;
    using System.Linq;
    using System.IO;
    using Microsoft.Build.Framework;
    using Microsoft.WindowsAzure.Storage;
    using LargeFileUploader;
    using System.Security.Cryptography;
using Microsoft.WindowsAzure.Storage.Blob;
    using System.Threading.Tasks;

    public class SyncFolderToAzureBlobStorageTask : ITask
    {
        public IBuildEngine BuildEngine { get; set; }

        public ITaskHost HostObject { get; set; }

        [Required]public string ConnectionStringFile { get; set; }
        [Required]public string ContainerName { get; set; }
        [Required]public string SourceFolder { get; set; }


        internal enum BlobComparisonResult
        {
            NotEqual, 
            SizeSameContentUnclear,
            PrettySureEqual
        }

        public class ContentInfo
        {
            public string ContentMD5 { get; set; }
            public long Length { get; set; }

            public static readonly ContentInfo NotExist = new ContentInfo { ContentMD5 = null, Length = -1};
        }

        public async Task UploadIfNeeded(CloudStorageAccount account, string containerName, string blobName, FileInfo fileInfo)
        {
            var client = account.CreateCloudBlobClient();
            var container = client.GetContainerReference(containerName);
            CloudBlockBlob blob = container.GetBlockBlobReference(blobName);

            var b = await GetContentInfo(blob);
            var f = await GetContentInfo(fileInfo);

            var result = ((Func<BlobComparisonResult>)(() =>
            {
                if (b == ContentInfo.NotExist) return BlobComparisonResult.NotEqual;
                if (b.Length != f.Length) return BlobComparisonResult.NotEqual;
                if (string.IsNullOrEmpty(b.ContentMD5)) return BlobComparisonResult.SizeSameContentUnclear;
                if (!string.Equals(b.ContentMD5, f.ContentMD5)) return BlobComparisonResult.NotEqual;
                return BlobComparisonResult.PrettySureEqual;
            }))();

            if (result == BlobComparisonResult.NotEqual  ||
                result == BlobComparisonResult.SizeSameContentUnclear)
            {
                await LargeFileUploaderUtils.UploadAsync(
                    fetchLocalData: (offset, length) => fileInfo.GetFileContentAsync(offset, length),
                    blobLenth: fileInfo.Length,
                    storageAccount: account,
                    containerName: containerName,
                    blobName: blobName);

                blob.Properties.ContentMD5 = f.ContentMD5;                
                await blob.SetPropertiesAsync();

                blob.Metadata["LastModified"] = fileInfo.LastWriteTimeUtc.ToString();
                await blob.SetMetadataAsync();

                log(string.Format("Uploaded {0} to {1}", fileInfo.FullName, blob.Uri.AbsoluteUri));
            }
            else
            {
                log(string.Format("Skipped {0}", fileInfo.FullName));
            }
        }

        public void log(string msg)
        {
            BuildEngine.LogMessageEvent(new BuildMessageEventArgs(
                message: msg, 
                helpKeyword: string.Empty, 
                senderName: this.GetType().Name,
                importance: MessageImportance.High));
        }

        public static async Task<ContentInfo> GetContentInfo(CloudBlockBlob blob)
        {
            if (!await blob.ExistsAsync())
                return ContentInfo.NotExist;

            await blob.FetchAttributesAsync();
            return new ContentInfo
            {
                Length = blob.Properties.Length,
                ContentMD5 = blob.Properties.ContentMD5
            };
        }
        public static Task<ContentInfo> GetContentInfo(FileInfo fileInfo)
        {
            Func<string> GetMD5 = () =>
            {
                using (var md5 = MD5.Create())
                {
                    using (var file = fileInfo.OpenRead())
                    {
                        var hash = md5.ComputeHash(file);
                        return Convert.ToBase64String(hash);
                    }
                }
            };

            return Task.FromResult<ContentInfo>(new ContentInfo
            {
                ContentMD5 = GetMD5(), 
                Length = fileInfo.Length
            });
        }

        public bool Execute()
        {
            var connectionString = File.ReadAllText(this.ConnectionStringFile);
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudBlobClient();
            var container = client.GetContainerReference(this.ContainerName);
            container.CreateIfNotExists();


            var rootFolder =  new DirectoryInfo(this.SourceFolder).FullName;
            var tasks = new DirectoryInfo(rootFolder).EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Select(f => new { BlobName = f.FullName.Replace(rootFolder + "\\", string.Empty),  FileInfo  = f })
                .Select(_ => UploadIfNeeded(account: account, containerName: this.ContainerName, blobName: _.BlobName, fileInfo: _.FileInfo))
                .ToArray();


            Task.WaitAll(tasks);

            return true;
        }
    }
}