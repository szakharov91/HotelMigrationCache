using System.Net;
using HotelMigrationCache.Core.Interfaces;
using HotelMigrationCache.Core.Store;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.Shared.Utils;

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

        CloudProfileData? profile = null;

        using (ICacheServiceClient client = new CacheServiceTcpClient(IPAddress.Loopback.ToString(), 3456))
        {
            await client.SetAsync("test", new CloudProfileData() { SrcId = "123", DstId = "456" });

            await Task.Delay(TimeSpan.FromSeconds(2));

            profile = await client.GetAsync("test");
        }

        Console.WriteLine($"Retrieved profile: {profile.SrcId} -> {profile.DstId}");

        await Task.Delay(TimeSpan.FromSeconds(10));

        Console.ReadLine();
    }
}
