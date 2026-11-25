using NetworkHidBridge.Core.Input;

namespace NetworkHidBridge.Linux;

internal static class LinuxKeyMap
{
    private static readonly byte[] Usage =
    [
        0, 41, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 45, 46, 42, 43,
        20, 26, 8, 21, 23, 28, 24, 12, 18, 19, 47, 48, 40, 0, 4, 22,
        7, 9, 10, 11, 13, 14, 15, 51, 52, 53, 0, 49, 29, 27, 6, 25,
        5, 17, 16, 54, 55, 56, 0, 85, 0, 44, 57, 58, 59, 60, 61, 62,
        63, 64, 65, 66, 67, 83, 71, 95, 96, 97, 86, 92, 93, 94, 87, 89,
        90, 91, 98, 99, 0, 0, 100, 68, 69
    ];

    internal static MappedKey Map(ushort code)
    {
        byte mask = code switch
        {
            29 => 1, 42 => 2, 56 => 4, 125 => 8,
            97 => 16, 54 => 32, 100 => 64, 126 => 128,
            _ => 0
        };
        byte usage = code < Usage.Length ? Usage[code] : (byte)(code switch
        {
            96 => 88, 98 => 84, 99 => 70, 102 => 74, 103 => 82,
            104 => 75, 105 => 80, 106 => 79, 107 => 77, 108 => 81,
            109 => 78, 110 => 73, 111 => 76, 119 => 72, 127 => 101,
            _ => 0
        });
        return new(usage, mask);
    }

    internal static ushort Consumer(ushort code) => code switch
    {
        113 => ConsumerUsage.Mute,
        114 => ConsumerUsage.VolumeDown,
        115 => ConsumerUsage.VolumeUp,
        163 => ConsumerUsage.ScanNextTrack,
        164 => ConsumerUsage.PlayPause,
        165 => ConsumerUsage.ScanPreviousTrack,
        166 => ConsumerUsage.Stop,
        _ => 0
    };
}
