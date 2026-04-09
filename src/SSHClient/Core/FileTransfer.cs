using System;
using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json;
using WebSocketSharp;
using SSHCommon.Protocol;

namespace SSHClient.Core
{
    public static class FileTransfer
    {
        private const int ChunkSize = 65536; // 64KB

        public static void Upload(WebSocket ws, string localPath, string remotePath)
        {
            if (!File.Exists(localPath))
            {
                Console.WriteLine($"File not found: {localPath}");
                return;
            }

            var fileInfo = new FileInfo(localPath);
            var fileData = File.ReadAllBytes(localPath);
            var totalChunks = (int)Math.Ceiling((double)fileData.Length / ChunkSize);

            // Send upload start
            var startMsg = new ProtocolMessage(MessageType.UploadStart,
                JsonConvert.SerializeObject(new FileTransferStart
                {
                    FileName = Path.GetFileName(localPath),
                    FileSize = fileData.Length,
                    ChunkSize = ChunkSize,
                    TotalChunks = totalChunks,
                    RemotePath = remotePath ?? Path.GetFileName(localPath)
                }));
            ws.Send(startMsg.ToJson());

            // Send chunks
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
                ws.Send(chunkMsg.ToJson());

                // Progress
                var progress = (double)(i + 1) / totalChunks;
                DrawProgressBar(progress, fileInfo.Length, offset + length);
            }

            // Send upload complete
            var completeMsg = new ProtocolMessage(MessageType.UploadComplete, "");
            ws.Send(completeMsg.ToJson());

            Console.WriteLine();
        }

        public static void Download(WebSocket ws, string remotePath, string localPath)
        {
            // Send download request
            var startMsg = new ProtocolMessage(MessageType.DownloadStart, $"\"{remotePath}\"");
            ws.Send(startMsg.ToJson());
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
                    Console.WriteLine();
                    break;
            }
        }

        private static void DrawProgressBar(double progress, long totalSize, long transferred)
        {
            var barWidth = 40;
            var filled = (int)(barWidth * progress);
            var bar = new string('█', filled) + new string('░', barWidth - filled);

            var sizeStr = FormatFileSize(transferred) + "/" + FormatFileSize(totalSize);
            Console.Write($"\r  [{bar}] {progress * 100:F1}% {sizeStr}   ");
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
