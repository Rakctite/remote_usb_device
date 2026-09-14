namespace NetworkHidBridge.Core.Protocol;

public enum MessageType : byte
{
    ClientHello = 0x01,
    ServerHello = 0x02,
    ClientAuth = 0x03,
    KeyboardState = 0x10,
    MouseState = 0x11,
    ConsumerState = 0x12,
    FullState = 0x13,
    ReleaseAll = 0x14,
    Heartbeat = 0x20,
    HeartbeatAck = 0x21,
}
