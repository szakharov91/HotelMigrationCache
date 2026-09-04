using System.Net;
using HotelMigrationCache.Core.Interfaces;
using HotelMigrationCache.Core.Store;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.Demo;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("Hello, World!");

        _ = Task.Run(async () =>
        {
            using IKeyValueStore store = new InMemoryKeyValueStore();
            using IProtocolInterface protocolInterface = new TcpServerInterface(store, 3456, IPAddress.Any);
            await protocolInterface.RunAsync();
        });

        Console.ReadLine();
    }
}
