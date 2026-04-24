using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        private int _nextChunkIndex;
        private long _bytesWritten;
        private string _uploadError;
        private SHA256 _uploadHash;

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
            _nextChunkIndex = 0;
            _bytesWritten = 0;
            _uploadError = null;
            _uploadHash = SHA256.Create();
        }

        public bool WriteChunk(string data)
        {
            if (!_uploading || _uploadStream == null)
                return false;

            var chunk = JsonConvert.DeserializeObject<FileChunk>(data);
            if (chunk?.Data == null)
            {
                _uploadError = "Invalid upload chunk";
                return false;
            }

            if (chunk.Index != _nextChunkIndex)
            {
                _uploadError = $"Upload chunk order mismatch: expected {_nextChunkIndex}, got {chunk.Index}";
                return false;
            }

            var bytes = Convert.FromBase64String(chunk.Data);
            _uploadStream.Write(bytes, 0, bytes.Length);
            _uploadHash.TransformBlock(bytes, 0, bytes.Length, null, 0);
            _bytesWritten += bytes.Length;
            _nextChunkIndex++;
            return true;
        }

        public FileTransferComplete FinishUpload(string completeData)
        {
            var path = _currentUploadPath;
            var success = _uploading && string.IsNullOrEmpty(_uploadError);
            var message = success ? "Upload completed" : (_uploadError ?? "Upload was not started");
            var expectedHash = GetExpectedUploadHash(completeData);

            if (success && _uploadInfo != null && _bytesWritten != _uploadInfo.FileSize)
            {
                success = false;
                message = $"Upload size mismatch: expected {_uploadInfo.FileSize}, got {_bytesWritten}";
            }

            if (success && _uploadInfo != null && _nextChunkIndex != _uploadInfo.TotalChunks)
            {
                success = false;
                message = $"Upload chunk count mismatch: expected {_uploadInfo.TotalChunks}, got {_nextChunkIndex}";
            }

            if (success && !string.IsNullOrEmpty(expectedHash))
            {
                _uploadHash.TransformFinalBlock(new byte[0], 0, 0);
                var actualHash = ToHex(_uploadHash.Hash);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    success = false;
                    message = $"Upload hash mismatch: expected {expectedHash}, got {actualHash}";
                }
            }

            CleanupUpload();

            return new FileTransferComplete
            {
                Success = success,
                Message = message,
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
            try
            {
                _uploadHash?.Dispose();
            }
            catch { }
            _uploadHash = null;
        }

        public void FailUpload(string message)
        {
            _uploadError = message;
        }

        private string GetExpectedUploadHash(string completeData)
        {
            if (!string.IsNullOrEmpty(completeData))
            {
                try
                {
                    var complete = JsonConvert.DeserializeObject<FileTransferComplete>(completeData);
                    if (!string.IsNullOrEmpty(complete?.Sha256))
                        return complete.Sha256;
                }
                catch { }
            }

            return _uploadInfo?.Sha256;
        }

        public static FileTransferStart PrepareDownload(string remotePath)
        {
            if (!File.Exists(remotePath))
                return null;

            var fileInfo = new FileInfo(remotePath);

            const int chunkSize = 65536; // 64KB
            var totalChunks = CalculateTotalChunks(fileInfo.Length, chunkSize);

            return new FileTransferStart
            {
                FileName = Path.GetFileName(remotePath),
                FileSize = fileInfo.Length,
                ChunkSize = chunkSize,
                TotalChunks = totalChunks,
                RemotePath = remotePath
            };
        }

        public static FileChunk BuildChunk(byte[] buffer, int length, int index)
        {
            var chunk = new byte[length];
            Buffer.BlockCopy(buffer, 0, chunk, 0, length);

            return new FileChunk
            {
                Index = index,
                Data = Convert.ToBase64String(chunk)
            };
        }

        private static int CalculateTotalChunks(long fileSize, int chunkSize)
        {
            var chunks = (fileSize + chunkSize - 1) / chunkSize;
            if (chunks > int.MaxValue)
                throw new InvalidOperationException("File is too large for the current download protocol");
            return (int)chunks;
        }

        public static string ToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
