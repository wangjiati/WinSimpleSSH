using System;
using System.IO;
using Newtonsoft.Json;
using SSHCommon.Protocol;

namespace SSHServer.Core
{
    public class FileTransferHandler
    {
        private string _currentUploadPath;
        private FileStream _uploadStream;
        private FileTransferStart _uploadInfo;
        private bool _uploading;

        public string CurrentUploadPath => _currentUploadPath;
        public bool IsUploading => _uploading;

        public FileTransferStart ParseUploadStart(string data)
        {
            _uploadInfo = JsonConvert.DeserializeObject<FileTransferStart>(data);
            return _uploadInfo;
        }

        public void StartUpload(string baseDirectory)
        {
            // 清理之前的上传状态
            CleanupUpload();

            var remotePath = string.IsNullOrEmpty(_uploadInfo.RemotePath)
                ? _uploadInfo.FileName
                : _uploadInfo.RemotePath;

            if (Path.IsPathRooted(remotePath))
            {
                _currentUploadPath = remotePath;
            }
            else if (Directory.Exists(remotePath))
            {
                _currentUploadPath = Path.Combine(remotePath, _uploadInfo.FileName);
            }
            else
            {
                _currentUploadPath = Path.Combine(baseDirectory, remotePath);
            }

            var dir = Path.GetDirectoryName(_currentUploadPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 先尝试创建文件检查写权限，失败则提前报错
            _uploadStream = new FileStream(_currentUploadPath, FileMode.Create, FileAccess.Write);
            _uploading = true;
        }

        public bool WriteChunk(string data)
        {
            if (!_uploading || _uploadStream == null)
                return false;

            var chunk = JsonConvert.DeserializeObject<FileChunk>(data);
            if (chunk?.Data == null)
                return false;

            var bytes = Convert.FromBase64String(chunk.Data);
            _uploadStream.Write(bytes, 0, bytes.Length);
            return true;
        }

        public FileTransferComplete FinishUpload()
        {
            var path = _currentUploadPath;
            var success = _uploading;

            CleanupUpload();

            return new FileTransferComplete
            {
                Success = success,
                Message = success ? "Upload completed" : "Upload was not started",
                FileName = path
            };
        }

        private void CleanupUpload()
        {
            _uploading = false;
            try
            {
                _uploadStream?.Close();
                _uploadStream?.Dispose();
            }
            catch { }
            _uploadStream = null;
        }

        public static FileTransferStart PrepareDownload(string remotePath, out byte[] fileData)
        {
            if (!File.Exists(remotePath))
            {
                fileData = null;
                return null;
            }

            var fileInfo = new FileInfo(remotePath);
            fileData = File.ReadAllBytes(remotePath);

            const int chunkSize = 65536; // 64KB
            var totalChunks = (int)Math.Ceiling((double)fileData.Length / chunkSize);

            return new FileTransferStart
            {
                FileName = Path.GetFileName(remotePath),
                FileSize = fileData.Length,
                ChunkSize = chunkSize,
                TotalChunks = totalChunks,
                RemotePath = remotePath
            };
        }

        public static FileChunk BuildChunk(byte[] fileData, int index, int chunkSize)
        {
            var offset = index * chunkSize;
            var length = Math.Min(chunkSize, fileData.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(fileData, offset, chunk, 0, length);

            return new FileChunk
            {
                Index = index,
                Data = Convert.ToBase64String(chunk)
            };
        }
    }
}
