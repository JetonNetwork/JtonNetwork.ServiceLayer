using JtonNetwork.ServiceLayer.Attributes;
using JtonNetwork.ServiceLayer.Storage;
using Serilog;
using SubstrateNetApi;
using SubstrateNetApi.Model.Rpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace JtonNetwork.ServiceLayer
{
    internal class GameStorage
    {
        // Storage Values => xxhash128("ModuleName") + xxhash128("StorageName")
        // 128 bits = 16 bytes
        // 16 bytes in hex is 32 characters.
        private const int STORAGE_VALUES_STRING_LENGTH = 2 + 32 * 2;

        // Storage Maps   => xxhash128("ModuleName") + xxhash128("StorageName") + blake256hash("StorageItemKey")
        // 256 bits = 32 bytes
        // 32 bytes in hex is 64 characters.
        private const int STORAGE_MAPS_STRING_LENGTH = STORAGE_VALUES_STRING_LENGTH + 64;

        // Storage Double Maps   => xxhash128("ModuleName") + xxhash128("StorageName") + blake256hash("StorageItemKey1") + blake256hash("StorageItemKey2")
        private const int STORAGE_DOUBLEMAPS_STRING_LENGTH = STORAGE_VALUES_STRING_LENGTH + 64 + 64;

        private readonly ManualResetEvent StorageStartProcessingEvent = new ManualResetEvent(false);
        private readonly object Lock = new object();

        private readonly Dictionary<string, string> StorageModuleDisplayNames = new Dictionary<string, string>();
        private readonly Dictionary<string, string> StorageModuleItemDisplayNames = new Dictionary<string, string>();
        private readonly Dictionary<string, Tuple<object, MethodInfo>> StorageChangeListener = new Dictionary<string, Tuple<object, MethodInfo>>();

        private List<IStorage> Storages = new List<IStorage>();

        internal IStorage GetStorage<T>()
        {
            foreach (var storage in Storages)
            {
                if (storage.GetType().GetInterfaces().Contains(typeof(T)))
                {
                    return storage;
                }
            }

            throw new KeyNotFoundException($"Could not find storage {typeof(T).Name} in storage list.");
        }

        internal async Task InitializeAsync(SubstrateClient client, List<IStorage> storages)
        {
            Storages = storages;

            InitializeMetadataDisplayNames(client);
            InitializeStorageChangeListener();

            foreach (var storage in Storages)
            {
                await storage.InitializeAsync(client);
            }
        }

        private void InitializeStorageChangeListener()
        {
            foreach (var storage in Storages)
            {
                foreach (var method in storage.GetType().GetMethods())
                {
                    var attributes = method.GetCustomAttributes(typeof(StorageChangeAttribute), true);
                    foreach (var attribute in attributes)
                    {
                        var listenerMethod = attribute as StorageChangeAttribute;
                        StorageChangeListener.Add(listenerMethod.Key, new Tuple<object, MethodInfo>(storage, method));
                    }
                }
            }
        }

        private void InitializeMetadataDisplayNames(SubstrateClient client)
        {
            foreach (var module in client.MetaData.Modules)
            {
                if (module.Storage == null)
                    continue;

                foreach (var storageItem in module.Storage.Items)
                {
                    var key = Utils.Bytes2HexString(RequestGenerator.GetStorageKeyBytesHash(module, storageItem)).ToLower();
                    var moduleNameHash = $"0x{key.Substring(2, 32)}";
                    var storageItemNameHash = $"0x{key.Substring(34, 32)}";

                    if (!StorageModuleDisplayNames.ContainsKey(moduleNameHash))
                    {
                        StorageModuleDisplayNames.Add(moduleNameHash, module.Name);
                    }

                    if (!StorageModuleItemDisplayNames.ContainsKey(storageItemNameHash))
                    {
                        StorageModuleItemDisplayNames.Add(storageItemNameHash, storageItem.Name);
                    }
                }
            }

            Log.Information("loaded storage metadata module names {count}", StorageModuleDisplayNames.Count);
            Log.Information("loaded storage metadata module item names {count}", StorageModuleItemDisplayNames.Count);
        }

        internal void OnStorageUpdate(string id, StorageChangeSet changes)
        {
            lock (Lock)
            {
                // Block the current thread until we received the initialize signal.
                // This function returns immediately once the signal was set at least once.
                StorageStartProcessingEvent.WaitOne();

                // Process the changes.
                foreach (var change in changes.Changes)
                {
                    // The key starts with 0x prefix.
                    var key = change[0].ToLower();

                    switch (key.Length)
                    {
                        // [0x][Hash128(ModuleName)][Hash128(StorageName)]
                        case STORAGE_VALUES_STRING_LENGTH:
                            {
                                var moduleNameHash = $"0x{key.Substring(2, 32)}";
                                var storageItemNameHash = $"0x{key.Substring(34, 32)}";
                                ProcessStorageChange(moduleNameHash, storageItemNameHash, new string[] { }, change[1]);
                            }
                            break;

                        // [0x][Hash128(ModuleName)][Hash128(StorageName)][Hash256(StorageItemKey)]
                        case STORAGE_MAPS_STRING_LENGTH:
                            {
                                var moduleNameHash = $"0x{key.Substring(2, 32)}";
                                var storageItemNameHash = $"0x{key.Substring(34, 32)}";
                                var storageItemKeyHash = key.Substring(66, 64);
                                ProcessStorageChange(moduleNameHash, storageItemNameHash, new string[] { storageItemKeyHash }, change[1]);
                            }
                            break;
                        // [0x][Hash128(ModuleName)][Hash128(StorageName)][Hash256(StorageItemKey1)][Hash256(StorageItemKey2)]
                        case STORAGE_DOUBLEMAPS_STRING_LENGTH:
                            {
                                var moduleNameHash = $"0x{key.Substring(2, 32)}";
                                var storageItemNameHash = $"0x{key.Substring(34, 32)}";
                                var storageItemKeyHash1 = key.Substring(66, 64);
                                var storageItemKeyHash2 = key.Substring(130, 64);
                                ProcessStorageChange(moduleNameHash, storageItemNameHash, new string[] { storageItemKeyHash1, storageItemKeyHash2 }, change[1]);
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        internal void StartProcessingChanges()
        {
            StorageStartProcessingEvent.Set();
        }

        private void ProcessStorageChange(string moduleNameHash, string storageItemNameHash, string[] storageItemKeys, string data)
        {
            var module = GetModuleDisplayName(moduleNameHash);
            if (string.IsNullOrEmpty(module))
                return;

            var storageItem = GetStorageItemDisplayName(storageItemNameHash);
            if (string.IsNullOrEmpty(storageItem))
                return;

            var key = $"{module}.{storageItem}";
            if (StorageChangeListener.ContainsKey(key))
            {
                var listener = StorageChangeListener[key];

                string[] parameters = new string[storageItemKeys.Length + 1];
                parameters[parameters.Length - 1] = data;
                switch (storageItemKeys.Length)
                {
                    case 0: 
                        break;
                    case 1:
                        parameters[0] = storageItemKeys[0];
                        break;
                    case 2:
                        parameters[1] = storageItemKeys[1];
                        break;
                    default:
                        throw new NotImplementedException("To many storage keys, in array!");
                }
                
                listener.Item2.Invoke(listener.Item1, parameters);
            }
        }

        private string GetStorageItemDisplayName(string storageItemHash)
        {
            if (StorageModuleItemDisplayNames.ContainsKey(storageItemHash))
            {
                return StorageModuleItemDisplayNames[storageItemHash];
            }

            return string.Empty;
        }

        private string GetModuleDisplayName(string moduleNameHash)
        {
            if (StorageModuleDisplayNames.ContainsKey(moduleNameHash))
            {
                return StorageModuleDisplayNames[moduleNameHash];
            }

            return string.Empty;
        }
    }
}
