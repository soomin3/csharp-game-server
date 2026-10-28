namespace Common.Network
{
    public enum PacketId : ushort
    {
        None = 0,
        Echo = 1,
        Login = 2,
        Chat = 3,
        MoveReq = 4,
        MoveNtf = 5,
    }
}
