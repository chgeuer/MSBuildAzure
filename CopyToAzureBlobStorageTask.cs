namespace RhysG.MSBuild.Azure
{
    using System;
    using System.Linq;
    using System.IO;
    using Microsoft.Build.Framework;
    using Microsoft.WindowsAzure.Storage;
    using LargeFileUploader;

    public class CopyToAzureBlobStorageTask : ITask
    {
        public IBuildEngine BuildEngine { get; set; }

        public ITaskHost HostObject { get; set; }

        [Required]
        public string ContainerName { get; set; }

        [Required]
        public string ConnectionStringFile { get; set; }

        [Required]
        public string ContentType { get; set; }

        public string DestinationFolder { get; set; }

        public string ContentEncoding { get; set; }

        [Required]
        public ITaskItem[] Files { get; set; }

        public bool Execute()
        {
            Action<string> log = msg => BuildEngine.LogMessageEvent(new BuildMessageEventArgs(
                        message: msg, helpKeyword: string.Empty, senderName: this.GetType().Name,
                        importance: MessageImportance.High));

            var connectionString = File.ReadAllText(this.ConnectionStringFile);
            var account = CloudStorageAccount.Parse(connectionString);
            var client = account.CreateCloudBlobClient();
            var container = client.GetContainerReference(this.ContainerName);

            Action<FileInfo> upload = inputFile =>
            {
                container.CreateIfNotExists();

                LargeFileUploader.LargeFileUploaderUtils.Log = log;


                LargeFileUploaderUtils.UploadAsync(
                    file: inputFile,
                    storageAccount: account,
                    containerName: this.ContainerName,
                    uploadParallelism: 1).Wait();
            };

            Func<string, DateTime> getLastModified = blobName => 
            {
                try
                {
                    var blobReference = container.GetBlobReferenceFromServer(blobName);
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

            Action<string, FileInfo> setLastModified = (blobName, fileInfo) =>
            {
                var blobReference = container.GetBlobReferenceFromServer(blobName);
                if (!blobReference.Exists()) return;
                blobReference.Metadata["LastModified"] = fileInfo.LastWriteTimeUtc.Ticks.ToString();
                blobReference.SetMetadata();
                blobReference.Properties.ContentType = ContentType;
                if (!String.IsNullOrWhiteSpace(ContentEncoding))
                {
                    blobReference.Properties.ContentEncoding = ContentEncoding;
                }
                blobReference.SetProperties();
            };


            // Files.Select(async fileItem => { });

            foreach (ITaskItem fileItem in Files)
            {
                FileInfo file = new FileInfo(fileItem.ItemSpec);
                string blobName = (string.IsNullOrEmpty(DestinationFolder) ? "" : string.Format("{0}/", DestinationFolder)) + file.Name;
               
                DateTime lastModified = getLastModified(blobName);
                if (lastModified != file.LastWriteTimeUtc)
                {
                    log("must upload");
                    upload(file);
                    setLastModified(blobName, file);

                    log(string.Format("Updating: {0}", file.Name));
                }
            }

            return true;
        }
    }
}