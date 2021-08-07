using JtonNetwork.ServiceLayer.Storage;
using Serilog;
using SubstrateNetApi;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JtonNetwork.ServiceLayer
{
    public class GameService
    {
        private SubstrateClient Client;

        private readonly GameStorage GameStorage = new GameStorage();

        public async Task InitializeAsync(GameServiceConfiguration configuration)
        {
            Log.Information("initialize GameService");

            //
            // Initialize substrate client API
            //
            Log.Information("substrate client connecting to {uri}", configuration.Endpoint);

            Client = new SubstrateClient(configuration.Endpoint);
            await Client.ConnectAsync(configuration.CancellationToken);

            Log.Information("substrate client connected");

            //
            // Initialize storage systems
            // Start by subscribing to any storage change and then start loading
            // all storages that this service is interested in.
            //
            // While we are loading storages any storage subscription notification will
            // wait to be processed until the initialization is complete.
            await Client.State.SubscribeStorageAsync(null, GameStorage.OnStorageUpdate);

            // Load storages we are interested in.
            await GameStorage.InitializeAsync(Client, configuration.Storages);

            // Start processing subscriptions.
            GameStorage.StartProcessingChanges();
        }

        public IStorage GetStorage<T>() => GameStorage.GetStorage<T>();
    }
}
