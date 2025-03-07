using Files.App.Helpers;
using Files.App.Extensions;
using SevenZip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using IO = System.IO;

namespace Files.App.Filesystem.StorageItems
{
    public sealed class ZipStorageFile : BaseStorageFile
    {
        private readonly string containerPath;
        private readonly BaseStorageFile backingFile;

        public override string Path { get; }
        public override string Name { get; }
        public override string DisplayName => Name;
        public override string ContentType => "application/octet-stream";
        public override string FileType => IO.Path.GetExtension(Name);
        public override string FolderRelativeId => $"0\\{Name}";

        public override string DisplayType
        {
            get
            {
                var itemType = "ItemTypeFile".GetLocalizedResource();
                if (Name.Contains(".", StringComparison.Ordinal))
                {
                    itemType = FileType.Trim('.') + " " + itemType;
                }
                return itemType;
            }
        }

        public override DateTimeOffset DateCreated { get; }
        public override Windows.Storage.FileAttributes Attributes => Windows.Storage.FileAttributes.Normal | Windows.Storage.FileAttributes.ReadOnly;

        private IStorageItemExtraProperties properties;
        public override IStorageItemExtraProperties Properties => properties ??= new BaseBasicStorageItemExtraProperties(this);

        public ZipStorageFile(string path, string containerPath)
        {
            Name = IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            Path = path;
            this.containerPath = containerPath;
        }
        public ZipStorageFile(string path, string containerPath, BaseStorageFile backingFile) : this(path, containerPath)
            => this.backingFile = backingFile;
        public ZipStorageFile(string path, string containerPath, ArchiveFileInfo entry) : this(path, containerPath)
            => DateCreated = entry.CreationTime == DateTime.MinValue ? DateTimeOffset.MinValue : entry.CreationTime;
        public ZipStorageFile(string path, string containerPath, ArchiveFileInfo entry, BaseStorageFile backingFile) : this(path, containerPath, entry)
            => this.backingFile = backingFile;

        public override IAsyncOperation<StorageFile> ToStorageFileAsync()
            => StorageFile.CreateStreamedFileAsync(Name, ZipDataStreamingHandler(Path), null);

        public static IAsyncOperation<BaseStorageFile> FromPathAsync(string path)
        {
            return AsyncInfo.Run(cancellationToken =>
            {
                if (!FileExtensionHelpers.IsBrowsableZipFile(path, out var ext))
                {
                    return Task.FromResult<BaseStorageFile>(null);
                }
                var marker = path.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (marker is not -1)
                {
                    var containerPath = path.Substring(0, marker + ext.Length);
                    if (path == containerPath)
                    {
                        return Task.FromResult<BaseStorageFile>(null); // Root
                    }
                    if (CheckAccess(containerPath))
                    {
                        return Task.FromResult<BaseStorageFile>(new ZipStorageFile(path, containerPath));
                    }
                }
                return Task.FromResult<BaseStorageFile>(null);
            });
        }

        public override bool IsEqual(IStorageItem item) => item?.Path == Path;
        public override bool IsOfType(StorageItemTypes type) => type is StorageItemTypes.File;

        public override IAsyncOperation<BaseStorageFolder> GetParentAsync() => throw new NotSupportedException();
        public override IAsyncOperation<BaseBasicProperties> GetBasicPropertiesAsync() => GetBasicProperties().AsAsyncOperation();

        public override IAsyncOperation<IRandomAccessStream> OpenAsync(FileAccessMode accessMode)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                bool rw = accessMode is FileAccessMode.ReadWrite;
                if (Path == containerPath)
                {
                    if (backingFile is not null)
                    {
                        return await backingFile.OpenAsync(accessMode);
                    }

                    var file = NativeFileOperationsHelper.OpenFileForRead(containerPath, rw);
                    if (file.IsInvalid)
                    {
                        return null;
                    }

                    return new FileStream(file, rw ? FileAccess.ReadWrite : FileAccess.Read).AsRandomAccessStream();
                }

                if (!rw)
                {
                    SevenZipExtractor zipFile = await OpenZipFileAsync();
                    if (zipFile == null || zipFile.ArchiveFileData == null)
                    {
                        return null;
                    }

                    //zipFile.IsStreamOwner = true;
                    var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == Path);

                    if (entry.FileName is not null)
                    {
                        var ms = new MemoryStream();
                        await zipFile.ExtractFileAsync(entry.Index, ms);
                        ms.Position = 0;
                        return new NonSeekableRandomAccessStreamForRead(ms, (ulong)entry.Size)
                        {
                            DisposeCallback = () => zipFile.Dispose()
                        };
                    }
                    return null;
                }

                throw new NotSupportedException("Can't open zip file as RW");
            });
        }
        public override IAsyncOperation<IRandomAccessStream> OpenAsync(FileAccessMode accessMode, StorageOpenOptions options)
            => OpenAsync(accessMode);

        public override IAsyncOperation<IRandomAccessStreamWithContentType> OpenReadAsync()
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path == containerPath)
                {
                    if (backingFile is not null)
                    {
                        return await backingFile.OpenReadAsync();
                    }

                    var hFile = NativeFileOperationsHelper.OpenFileForRead(containerPath);
                    if (hFile.IsInvalid)
                    {
                        return null;
                    }

                    return new StreamWithContentType(new FileStream(hFile, FileAccess.Read).AsRandomAccessStream());
                }

                SevenZipExtractor zipFile = await OpenZipFileAsync();
                if (zipFile == null || zipFile.ArchiveFileData == null)
                {
                    return null;
                }

                //zipFile.IsStreamOwner = true;
                var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == Path);
                if (entry.FileName is null)
                {
                    return null;
                }

                var ms = new MemoryStream();
                await zipFile.ExtractFileAsync(entry.Index, ms);
                ms.Position = 0;
                var nsStream = new NonSeekableRandomAccessStreamForRead(ms, (ulong)entry.Size)
                {
                    DisposeCallback = () => zipFile.Dispose()
                };
                return new StreamWithContentType(nsStream);
            });
        }

        public override IAsyncOperation<IInputStream> OpenSequentialReadAsync()
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path == containerPath)
                {
                    if (backingFile is not null)
                    {
                        return await backingFile.OpenSequentialReadAsync();
                    }

                    var hFile = NativeFileOperationsHelper.OpenFileForRead(containerPath);
                    if (hFile.IsInvalid)
                    {
                        return null;
                    }

                    return new FileStream(hFile, FileAccess.Read).AsInputStream();
                }

                SevenZipExtractor zipFile = await OpenZipFileAsync();
                if (zipFile == null || zipFile.ArchiveFileData == null)
                {
                    return null;
                }
                //zipFile.IsStreamOwner = true;
                var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == Path);
                if (entry.FileName is null)
                {
                    return null;
                }

                var ms = new MemoryStream();
                await zipFile.ExtractFileAsync(entry.Index, ms);
                ms.Position = 0;
                return new NonSeekableRandomAccessStreamForRead(ms, (ulong)entry.Size)
                {
                    DisposeCallback = () => zipFile.Dispose()
                };
            });
        }

        public override IAsyncOperation<StorageStreamTransaction> OpenTransactedWriteAsync()
            => throw new NotSupportedException();
        public override IAsyncOperation<StorageStreamTransaction> OpenTransactedWriteAsync(StorageOpenOptions options)
            => throw new NotSupportedException();

        public override IAsyncOperation<BaseStorageFile> CopyAsync(IStorageFolder destinationFolder)
            => CopyAsync(destinationFolder, Name, NameCollisionOption.FailIfExists);
        public override IAsyncOperation<BaseStorageFile> CopyAsync(IStorageFolder destinationFolder, string desiredNewName)
            => CopyAsync(destinationFolder, desiredNewName, NameCollisionOption.FailIfExists);
        public override IAsyncOperation<BaseStorageFile> CopyAsync(IStorageFolder destinationFolder, string desiredNewName, NameCollisionOption option)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                using SevenZipExtractor zipFile = await OpenZipFileAsync();
                if (zipFile == null || zipFile.ArchiveFileData == null)
                {
                    return null;
                }

                //zipFile.IsStreamOwner = true;
                var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == Path);
                if (entry.FileName is null)
                {
                    return null;
                }

                var destFolder = destinationFolder.AsBaseStorageFolder();

                if (destFolder is ICreateFileWithStream cwsf)
                {
                    var ms = new MemoryStream();
                    await zipFile.ExtractFileAsync(entry.Index, ms);
                    ms.Position = 0;
                    using var inStream = new NonSeekableRandomAccessStreamForRead(ms, (ulong)entry.Size);
                    return await cwsf.CreateFileAsync(inStream.AsStreamForRead(), desiredNewName, option.Convert());
                }
                else
                {
                    var destFile = await destFolder.CreateFileAsync(desiredNewName, option.Convert());
                    using var outStream = await destFile.OpenStreamForWriteAsync();
                    await zipFile.ExtractFileAsync(entry.Index, outStream);
                    return destFile;
                }
            });
        }
        public override IAsyncAction CopyAndReplaceAsync(IStorageFile fileToReplace)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                using SevenZipExtractor zipFile = await OpenZipFileAsync();
                if (zipFile == null || zipFile.ArchiveFileData == null)
                {
                    return;
                }
                //zipFile.IsStreamOwner = true;
                var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == Path);
                if (entry.FileName is null)
                {
                    return;
                }

                using var hDestFile = fileToReplace.CreateSafeFileHandle(FileAccess.ReadWrite);
                using (var outStream = new FileStream(hDestFile, FileAccess.Write))
                {
                    await zipFile.ExtractFileAsync(entry.Index, outStream);
                }
            });
        }

        public override IAsyncAction MoveAsync(IStorageFolder destinationFolder)
            => throw new NotSupportedException();
        public override IAsyncAction MoveAsync(IStorageFolder destinationFolder, string desiredNewName)
            => throw new NotSupportedException();
        public override IAsyncAction MoveAsync(IStorageFolder destinationFolder, string desiredNewName, NameCollisionOption option)
            => throw new NotSupportedException();
        public override IAsyncAction MoveAndReplaceAsync(IStorageFile fileToReplace)
            => throw new NotSupportedException();

        public override IAsyncAction RenameAsync(string desiredName) => RenameAsync(desiredName, NameCollisionOption.FailIfExists);
        public override IAsyncAction RenameAsync(string desiredName, NameCollisionOption option)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path == containerPath)
                {
                    if (backingFile != null)
                    {
                        await backingFile.RenameAsync(desiredName, option);
                    }
                    else
                    {
                        var fileName = IO.Path.Combine(IO.Path.GetDirectoryName(Path), desiredName);
                        NativeFileOperationsHelper.MoveFileFromApp(Path, fileName);
                    }
                }
                else
                {
                    var index = await FetchZipIndex();
                    if (index < 0)
                    {
                        return;
                    }
                    using (var ms = new MemoryStream())
                    {
                        using (var archiveStream = await OpenZipFileAsync(FileAccessMode.Read))
                        {
                            SevenZipCompressor compressor = new SevenZipCompressor() { CompressionMode = CompressionMode.Append };
                            compressor.SetFormatFromExistingArchive(archiveStream);
                            var fileName = IO.Path.GetRelativePath(containerPath, IO.Path.Combine(IO.Path.GetDirectoryName(Path), desiredName));
                            await compressor.ModifyArchiveAsync(archiveStream, new Dictionary<int, string>() { { index, fileName } }, "", ms);
                        }
                        using (var archiveStream = await OpenZipFileAsync(FileAccessMode.ReadWrite))
                        {
                            ms.Position = 0;
                            await ms.CopyToAsync(archiveStream);
                            await ms.FlushAsync();
                            archiveStream.SetLength(archiveStream.Position);
                        }
                    }
                }
            });
        }

        public override IAsyncAction DeleteAsync() => DeleteAsync(StorageDeleteOption.Default);
        public override IAsyncAction DeleteAsync(StorageDeleteOption option)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path == containerPath)
                {
                    if (backingFile != null)
                    {
                        await backingFile.DeleteAsync();
                    }
                    else if (option == StorageDeleteOption.PermanentDelete)
                    {
                        NativeFileOperationsHelper.DeleteFileFromApp(Path);
                    }
                    else
                    {
                        throw new NotSupportedException("Moving to recycle bin is not supported.");
                    }
                }
                else
                {
                    var index = await FetchZipIndex();
                    if (index < 0)
                    {
                        return;
                    }
                    using (var ms = new MemoryStream())
                    {
                        using (var archiveStream = await OpenZipFileAsync(FileAccessMode.Read))
                        {
                            SevenZipCompressor compressor = new SevenZipCompressor() { CompressionMode = CompressionMode.Append };
                            compressor.SetFormatFromExistingArchive(archiveStream);
                            await compressor.ModifyArchiveAsync(archiveStream, new Dictionary<int, string>() { { index, null } }, "", ms);
                        }
                        using (var archiveStream = await OpenZipFileAsync(FileAccessMode.ReadWrite))
                        {
                            ms.Position = 0;
                            await ms.CopyToAsync(archiveStream);
                            await ms.FlushAsync();
                            archiveStream.SetLength(archiveStream.Position);
                        }
                    }
                }
            });
        }

        public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode)
            => Task.FromResult<StorageItemThumbnail>(null).AsAsyncOperation();
        public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize)
            => Task.FromResult<StorageItemThumbnail>(null).AsAsyncOperation();
        public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize, ThumbnailOptions options)
            => Task.FromResult<StorageItemThumbnail>(null).AsAsyncOperation();

        private static bool CheckAccess(string path)
        {
            try
            {
                var hFile = NativeFileOperationsHelper.OpenFileForRead(path);
                if (hFile.IsInvalid)
                {
                    return false;
                }
                using (SevenZipExtractor zipFile = new SevenZipExtractor(new FileStream(hFile, FileAccess.Read)))
                {
                    //zipFile.IsStreamOwner = true;
                    return zipFile.ArchiveFileData != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task<int> FetchZipIndex()
        {
            using (SevenZipExtractor zipFile = await OpenZipFileAsync())
            {
                if (zipFile == null || zipFile.ArchiveFileData == null)
                {
                    return -1;
                }
                //zipFile.IsStreamOwner = true;
                var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == Path);
                if (entry.FileName != null)
                {
                    return entry.Index;
                }
                return -1;
            }
        }

        private async Task<BaseBasicProperties> GetBasicProperties()
        {
            using SevenZipExtractor zipFile = await OpenZipFileAsync();
            if (zipFile == null || zipFile.ArchiveFileData == null)
            {
                return null;
            }

            //zipFile.IsStreamOwner = true;
            var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == Path);

            return entry.FileName is null
                ? new BaseBasicProperties()
                : new ZipFileBasicProperties(entry);
        }

        private IAsyncOperation<SevenZipExtractor> OpenZipFileAsync(bool openProtected = false)
        {
            return AsyncInfo.Run<SevenZipExtractor>(async (cancellationToken) =>
            {
                var zipFile = await OpenZipFileAsync(FileAccessMode.Read, openProtected);
                return zipFile is not null ? new SevenZipExtractor(zipFile) : null;
            });
        }

        private IAsyncOperation<Stream> OpenZipFileAsync(FileAccessMode accessMode, bool openProtected = false)
        {
            return AsyncInfo.Run<Stream>(async (cancellationToken) =>
            {
                bool readWrite = accessMode == FileAccessMode.ReadWrite;
                if (backingFile != null)
                {
                    return (await backingFile.OpenAsync(accessMode)).AsStream();
                }
                else
                {
                    var hFile = openProtected ?
                        await NativeFileOperationsHelper.OpenProtectedFileForRead(containerPath) :
                        NativeFileOperationsHelper.OpenFileForRead(containerPath, readWrite);
                    if (hFile.IsInvalid)
                    {
                        return null;
                    }
                    return (Stream)new FileStream(hFile, readWrite ? FileAccess.ReadWrite : FileAccess.Read);
                }
            });
        }

        private StreamedFileDataRequestedHandler ZipDataStreamingHandler(string name)
        {
            return async request =>
            {
                try
                {
                    // If called from here it fails with Access Denied?!
                    //var hFile = NativeFileOperationsHelper.OpenFileForRead(ContainerPath);
                    using SevenZipExtractor zipFile = await OpenZipFileAsync(openProtected: true);
                    if (zipFile == null || zipFile.ArchiveFileData == null)
                    {
                        request.FailAndClose(StreamedFileFailureMode.CurrentlyUnavailable);
                        return;
                    }
                    //zipFile.IsStreamOwner = true;
                    var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == name);
                    if (entry.FileName is null)
                    {
                        request.FailAndClose(StreamedFileFailureMode.CurrentlyUnavailable);
                    }
                    else
                    {
                        using (var outStream = request.AsStreamForWrite())
                        {
                            await zipFile.ExtractFileAsync(entry.Index, outStream);
                        }
                        request.Dispose();
                    }
                }
                catch
                {
                    request.FailAndClose(StreamedFileFailureMode.Failed);
                }
            };
        }

        private class ZipFileBasicProperties : BaseBasicProperties
        {
            private ArchiveFileInfo entry;

            public ZipFileBasicProperties(ArchiveFileInfo entry) => this.entry = entry;

            public override DateTimeOffset DateModified => entry.CreationTime == DateTime.MinValue ? DateTimeOffset.MinValue : entry.CreationTime;

            public override DateTimeOffset ItemDate => entry.CreationTime == DateTime.MinValue ? DateTimeOffset.MinValue : entry.CreationTime;

            public override ulong Size => (ulong)entry.Size;
        }
    }
}
