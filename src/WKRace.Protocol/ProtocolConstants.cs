namespace WKRace.Protocol
{
    public static class ProtocolConstants
    {
        public const int ProtocolVersion = 10;

        public const int DefaultServerPort = 47600;
        public const int DefaultLocalPort = 47677;

        // Frames are a little-endian ushort length followed by the payload.
        public const int MaxFrameSize = 2048;

        public const int MaxNameLength = 24;
        public const int MaxLevelNameLength = 64;
        public const int MaxTextLength = 200;
        public const int MaxAddressLength = 100;
        public const int MaxUrlLength = 1500;
        // Artifact bans: item names (prefab names such as "Item_Artifact_Rapier") and how many a pool may hold.
        public const int MaxItemNameLength = 48;
        public const int MaxBanPool = 16;
        public const int TokenLength = 32;

        public const int MinSeed = 1000000;
        public const int MaxSeed = 9999999;

        public const int MaxSplitIndex = 1000;
        public const int MaxAttempt = 100000;

        // Party races: players per lobby. A 1v1 lobby holds two.
        public const int MaxPartyPlayers = 15;
        // Chat lines, in characters.
        public const int MaxChatLength = 500;
        // Death causes ("deathfloor") and the game's death-screen lines ("FUSED INTO THE MASS").
        public const int MaxDeathCauseLength = 32;
        public const int MaxDeathTextLength = 64;
    }
}
