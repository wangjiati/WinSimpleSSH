namespace SSHCommon.Protocol
{
    public class UpdateRequest
    {
        public string Source;
        public string Checksum;
    }

    public class UpdateResponse
    {
        public bool Success;
        public string Message;
        public long FileSize;
    }
}
