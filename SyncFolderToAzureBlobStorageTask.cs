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


        public enum BlobComparisonResult
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

        public static async Task<BlobComparisonResult> BlobEqualsLocal(CloudBlockBlob blob, FileInfo fileInfo)
        {
            var b = await GetContentInfo(fileInfo);
            if (b == ContentInfo.NotExist) return BlobComparisonResult.NotEqual;
            var f = await GetContentInfo(fileInfo);
            if (b.Length != f.Length) return BlobComparisonResult.NotEqual;
            if (string.IsNullOrEmpty(b.ContentMD5)) return BlobComparisonResult.SizeSameContentUnclear;
            if (!string.Equals(b.ContentMD5, f.ContentMD5)) return BlobComparisonResult.NotEqual;
            return BlobComparisonResult.PrettySureEqual;
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
            Action<string> log = msg => BuildEngine.LogMessageEvent(new BuildMessageEventArgs(
                        message: msg, helpKeyword: string.Empty, senderName: this.GetType().Name,
                        importance: MessageImportance.High));

            var connectionString = File.ReadAllText(this.ConnectionStringFile);
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudBlobClient();
            var container = client.GetContainerReference(this.ContainerName);

            Func<FileInfo, System.Threading.Tasks.Task> uploadAsync = async (inputFile) =>
            {
                await container.CreateIfNotExistsAsync();

                LargeFileUploader.LargeFileUploaderUtils.Log = log;

                await LargeFileUploaderUtils.UploadAsync(
                    file: inputFile,
                    storageAccount: account,
                    containerName: this.ContainerName,
                    uploadParallelism: 1);
            };

            Func<string, System.Threading.Tasks.Task<DateTime>> getLastModifiedAsync = async (blobName) =>
            {
                try
                {
                    var blobReference = await container.GetBlobReferenceFromServerAsync(blobName);
                    if (!blobReference.Exists()) return DateTime.MinValue;
                    if (!blobReference.Metadata.ContainsKey("LastModified")) return DateTime.MinValue;
                    var lastModifiedStr = blobReference.Metadata["LastModified"];
                    long timeTicks = long.Parse(lastModifiedStr);
                    var lastModified = new DateTime(timeTicks, DateTimeKind.Utc);
                    return lastModified;
                }
                catch (StorageException)
                {
                    return DateTime.MinValue;
                }
            };

            var rootFolder =  new DirectoryInfo(this.SourceFolder).FullName;
            var files = new DirectoryInfo(rootFolder).EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Select(f => f.FullName.Replace(rootFolder + "\\", string.Empty)).ToList();





            //var uploadTasks = Files.Select(async (fileItem) => {
            //    FileInfo file = new FileInfo(fileItem.ItemSpec);
            //    string blobName = (string.IsNullOrEmpty(DestinationFolder) ? "" : string.Format("{0}/", DestinationFolder)) + file.Name;

            //    DateTime lastModified = await getLastModifiedAsync(blobName);
            //    if (lastModified != file.LastWriteTimeUtc)
            //    {
            //        log("must upload");
            //        await uploadAsync(file);
            //        await setLastModifiedAsync(blobName, file);

            //        log(string.Format("Updating: {0}", file.Name));
            //    }
            //}).ToArray();

            //System.Threading.Tasks.Task.WhenAll(uploadTasks).Wait();


            return true;
        }
    }
}