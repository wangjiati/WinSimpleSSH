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

        public FileTransferStart ParseUploadStart(string data)
        {
            _uploadInfo = JsonConvert.DeserializeObject<FileTransferStart>(data);
            return _uploadInfo;
        }

        public void StartUpload(string baseDirectory)
        {
            var remotePath = string.IsNullOrEmpty(_uploadInfo.RemotePath)
                ? _uploadInfo.FileName
                : _uploadInfo.RemotePath;

            _currentUploadPath = Path.IsPathRooted(remotePath)
                ? remotePath
                : Path.Combine(baseDirectory, remotePath);

            var dir = Path.GetDirectoryName(_currentUploadPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _uploadStream = new FileStream(_currentUploadPath, FileMode.Create, FileAccess.Write);
        }

        public bool WriteChunk(string data)
        {
            var chunk = JsonConvert.DeserializeObject<FileChunk>(data);
            var bytes = Convert.FromBase64String(chunk.Data);
            _uploadStream.Write(bytes, 0, bytes.Length);
            return true;
        }

        public FileTransferComplete FinishUpload()
        {
            _uploadStream?.Close();
            _uploadStream?.Dispose();
            _uploadStream = null;

            return new FileTransferComplete
            {
                Success = true,
                Message = "Upload completed",
                FileName = _currentUploadPath
            };
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
