using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using WebSocketSharp;
using SSHCommon.Crypto;
using SSHCommon.Protocol;

namespace SSHClient.Core
{
    public static class FileTransfer
    {
        private const int ChunkSize = 65536; // 64KB

        /// <summary>
        /// 静默模式：非交互 CLI 的 upload/download 动词会把它打开，
        /// 抑制进度条和辅助文字，或者改走 stderr。
        /// 注意：这是进程级静态状态，Agent 并行调用是多进程，彼此不冲突。
        /// </summary>
        public static bool Quiet { get; set; }

        /// <summary>发送上传开始消息，返回是否成功读取本地文件</summary>
        public static bool SendUploadStart(object sendLock, WebSocket ws, string localPath, string remotePath)
        {
            if (!File.Exists(localPath))
            {
                Console.Error.WriteLine($"File not found: {localPath}");
                return false;
            }

            var fileInfo = new FileInfo(localPath);
            var totalChunks = CalculateTotalChunks(fileInfo.Length);

            var startMsg = new ProtocolMessage(MessageType.UploadStart,
                JsonConvert.SerializeObject(new FileTransferStart
                {
                    FileName = Path.GetFileName(localPath),
                    FileSize = fileInfo.Length,
                    ChunkSize = ChunkSize,
                    TotalChunks = totalChunks,
                    RemotePath = remotePath ?? Path.GetFileName(localPath)
                }));
            SendLocked(sendLock, ws, startMsg.ToJson());

            _pendingUploadInfo = fileInfo;
            return true;
        }

        [ThreadStatic]
        private static FileInfo _pendingUploadInfo;

        /// <summary>发送所有数据块和完成消息（需在 SendUploadStart 成功且收到 UploadReady 后调用）</summary>
        public static void SendUploadChunks(object sendLock, WebSocket ws, string localPath)
        {
            var fileInfo = _pendingUploadInfo;
            if (fileInfo == null) return;

            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                var totalChunks = CalculateTotalChunks(fs.Length);
                var buffer = new byte[ChunkSize];
                long transferred = 0;

                for (int i = 0; i < totalChunks; i++)
                {
                    var read = fs.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        throw new EndOfStreamException($"Unexpected end of file while reading chunk {i}");

                    sha.TransformBlock(buffer, 0, read, null, 0);

                    var chunkMsg = new ProtocolMessage(MessageType.UploadChunk,
                        JsonConvert.SerializeObject(new FileChunk
                        {
                            Index = i,
                            Data = Convert.ToBase64String(buffer, 0, read)
                        }));
                    SendLocked(sendLock, ws, chunkMsg.ToJson());

                    transferred += read;
                    var progress = totalChunks == 0 ? 1.0 : (double)(i + 1) / totalChunks;
                    DrawProgressBar(progress, fileInfo.Length, transferred);
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);

                var completeMsg = new ProtocolMessage(MessageType.UploadComplete,
                    JsonConvert.SerializeObject(new FileTransferComplete
                    {
                        Success = true,
                        Sha256 = ToHex(sha.Hash)
                    }));
                SendLocked(sendLock, ws, completeMsg.ToJson());
            }

            if (!Quiet) Console.Error.WriteLine();
            _pendingUploadInfo = null;
        }

        public static void Download(object sendLock, WebSocket ws, string remotePath, string localPath)
        {
            var startMsg = new ProtocolMessage(MessageType.DownloadStart, remotePath);
            SendLocked(sendLock, ws, startMsg.ToJson());
        }

        private static void SendLocked(object sendLock, WebSocket ws, string data)
        {
            lock (sendLock)
            {
                if (ws?.IsAlive == true)
                    ws.Send(Obfuscator.Encode(Encoding.UTF8.GetBytes(data)));
            }
        }

        public static void HandleDownloadMessage(ProtocolMessage msg, string localPath, ref DownloadState state)
        {
            switch (msg.Type)
            {
                case MessageType.DownloadStart:
                    var info = JsonConvert.DeserializeObject<FileTransferStart>(msg.Data);
                    state?.Dispose();
                    state = new DownloadState
                    {
                        Info = info,
                        LocalPath = localPath ?? info.FileName,
                        BytesReceived = 0,
                        NextChunkIndex = 0,
                        Stream = new FileStream(localPath ?? info.FileName, FileMode.Create, FileAccess.Write),
                        Hash = SHA256.Create()
                    };
                    break;

                case MessageType.DownloadChunk:
                    if (state != null)
                    {
                        var chunk = JsonConvert.DeserializeObject<FileChunk>(msg.Data);
                        if (chunk?.Data == null)
                        {
                            state.Error = "Invalid download chunk";
                            state.Dispose();
                            break;
                        }

                        if (chunk.Index != state.NextChunkIndex)
                        {
                            state.Error = $"Download chunk order mismatch: expected {state.NextChunkIndex}, got {chunk.Index}";
                            state.Dispose();
                            break;
                        }

                        var bytes = Convert.FromBase64String(chunk.Data);
                        state.Stream.Write(bytes, 0, bytes.Length);
                        state.Hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
                        state.BytesReceived += bytes.Length;
                        state.NextChunkIndex++;

                        var progress = state.Info.TotalChunks == 0 ? 1.0 : (double)(chunk.Index + 1) / state.Info.TotalChunks;
                        DrawProgressBar(progress, state.Info.FileSize, state.BytesReceived);
                    }
                    break;

                case MessageType.DownloadComplete:
                    if (state != null)
                    {
                        var complete = JsonConvert.DeserializeObject<FileTransferComplete>(msg.Data);
                        FinishDownload(state, complete);
                    }
                    if (!Quiet) Console.Error.WriteLine();
                    break;
            }
        }

        private static void FinishDownload(DownloadState state, FileTransferComplete complete)
        {
            try
            {
                if (!string.IsNullOrEmpty(state.Error))
                    return;

                if (state.BytesReceived != state.Info.FileSize)
                    state.Error = $"Download size mismatch: expected {state.Info.FileSize}, got {state.BytesReceived}";
                else if (state.NextChunkIndex != state.Info.TotalChunks)
                    state.Error = $"Download chunk count mismatch: expected {state.Info.TotalChunks}, got {state.NextChunkIndex}";

                state.Hash.TransformFinalBlock(new byte[0], 0, 0);
                var expectedHash = !string.IsNullOrEmpty(complete?.Sha256) ? complete.Sha256 : state.Info.Sha256;
                if (string.IsNullOrEmpty(state.Error) && !string.IsNullOrEmpty(expectedHash))
                {
                    var actualHash = ToHex(state.Hash.Hash);
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        state.Error = $"Download hash mismatch: expected {expectedHash}, got {actualHash}";
                }
            }
            finally
            {
                state.Dispose();
            }
        }

        private static void DrawProgressBar(double progress, long totalSize, long transferred)
        {
            if (Quiet) return;
            var barWidth = 40;
            var filled = (int)(barWidth * progress);
            var bar = new string('█', filled) + new string('░', barWidth - filled);

            var sizeStr = FormatFileSize(transferred) + "/" + FormatFileSize(totalSize);
            Console.Error.Write($"\r  [{bar}] {progress * 100:F1}% {sizeStr}   ");
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + "B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1}GB";
        }

        private static int CalculateTotalChunks(long fileSize)
        {
            var chunks = (fileSize + ChunkSize - 1) / ChunkSize;
            if (chunks > int.MaxValue)
                throw new InvalidOperationException("File is too large for the current upload protocol");
            return (int)chunks;
        }

        private static string ToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    public class DownloadState : IDisposable
    {
        public FileTransferStart Info { get; set; }
        public string LocalPath { get; set; }
        public long BytesReceived { get; set; }
        public int NextChunkIndex { get; set; }
        public string Error { get; set; }
        public FileStream Stream { get; set; }
        public SHA256 Hash { get; set; }

        public void Dispose()
        {
            try
            {
                Stream?.Close();
                Stream?.Dispose();
            }
            catch { }
            Stream = null;

            try
            {
                Hash?.Dispose();
            }
            catch { }
            Hash = null;
        }
    }
}
