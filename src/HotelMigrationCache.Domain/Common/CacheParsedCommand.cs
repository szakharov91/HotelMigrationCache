using HotelMigrationCache.Shared.Protocol;

namespace HotelMigrationCache.Shared.Common;

public readonly ref struct CacheParsedCommand
{
    public ReadOnlySpan<byte> CommandName { get; init; }
    public ReadOnlySpan<byte> Key { get; init; }
    public ReadOnlySpan<byte> Value { get; init; }
    public bool IsValid { get; init; }
    public bool IsEmpty() => CommandName.IsEmpty && Key.IsEmpty && Value.IsEmpty;
    public ServerCommandKind GetCommandKind()
    {
        if (CommandName.SequenceEqual(ServerCommands.Get))
            return ServerCommandKind.Get;
        if (CommandName.SequenceEqual(ServerCommands.Set))
            return ServerCommandKind.Set;
        if (CommandName.SequenceEqual(ServerCommands.Delete))
            return ServerCommandKind.Delete;
        return ServerCommandKind.Unknown;
    }
}