using System;
using System.IO;
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
            var fileData = File.ReadAllBytes(localPath);
            var totalChunks = (int)Math.Ceiling((double)fileData.Length / ChunkSize);

            var startMsg = new ProtocolMessage(MessageType.UploadStart,
                JsonConvert.SerializeObject(new FileTransferStart
                {
                    FileName = Path.GetFileName(localPath),
                    FileSize = fileData.Length,
                    ChunkSize = ChunkSize,
                    TotalChunks = totalChunks,
                    RemotePath = remotePath ?? Path.GetFileName(localPath)
                }));
            SendLocked(sendLock, ws, startMsg.ToJson());

            // 暂存文件数据供 SendUploadChunks 使用
            _pendingUploadData = fileData;
            _pendingUploadInfo = fileInfo;
            return true;
        }

        [ThreadStatic]
        private static byte[] _pendingUploadData;
        [ThreadStatic]
        private static FileInfo _pendingUploadInfo;

        /// <summary>发送所有数据块和完成消息（需在 SendUploadStart 成功且收到 UploadReady 后调用）</summary>
        public static void SendUploadChunks(object sendLock, WebSocket ws, string localPath)
        {
            var fileData = _pendingUploadData;
            var fileInfo = _pendingUploadInfo;
            if (fileData == null) return;

            var totalChunks = (int)Math.Ceiling((double)fileData.Length / ChunkSize);

            for (int i = 0; i < totalChunks; i++)
            {
                var offset = i * ChunkSize;
                var length = Math.Min(ChunkSize, fileData.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(fileData, offset, chunk, 0, length);

                var chunkMsg = new ProtocolMessage(MessageType.UploadChunk,
                    JsonConvert.SerializeObject(new FileChunk
                    {
                        Index = i,
                        Data = Convert.ToBase64String(chunk)
                    }));
                SendLocked(sendLock, ws, chunkMsg.ToJson());

                var progress = (double)(i + 1) / totalChunks;
                DrawProgressBar(progress, fileInfo.Length, offset + length);
            }

            var completeMsg = new ProtocolMessage(MessageType.UploadComplete, "");
            SendLocked(sendLock, ws, completeMsg.ToJson());

            if (!Quiet) Console.Error.WriteLine();
            _pendingUploadData = null;
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
                    state = new DownloadState
                    {
                        Info = info,
                        LocalPath = localPath ?? info.FileName,
                        BytesReceived = 0
                    };
                    break;

                case MessageType.DownloadChunk:
                    if (state != null)
                    {
                        var chunk = JsonConvert.DeserializeObject<FileChunk>(msg.Data);
                        var bytes = Convert.FromBase64String(chunk.Data);
                        using (var fs = new FileStream(state.LocalPath, chunk.Index == 0 ? FileMode.Create : FileMode.Append))
                        {
                            fs.Write(bytes, 0, bytes.Length);
                        }
                        state.BytesReceived += bytes.Length;

                        var progress = (double)(chunk.Index + 1) / state.Info.TotalChunks;
                        DrawProgressBar(progress, state.Info.FileSize, state.BytesReceived);
                    }
                    break;

                case MessageType.DownloadComplete:
                    if (!Quiet) Console.Error.WriteLine();
                    break;
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
    }

    public class DownloadState
    {
        public FileTransferStart Info { get; set; }
        public string LocalPath { get; set; }
        public long BytesReceived { get; set; }
    }
}
