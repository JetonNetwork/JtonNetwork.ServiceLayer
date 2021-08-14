using JtonNetwork.ServiceLayer.Attributes;
using JtonNetwork.ServiceLayer.Storage;
using Serilog;
using SubstrateNetApi;
using SubstrateNetApi.Model.Rpc;
using SubstrateNetApi.Model.Meta;
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

        private readonly Dictionary<string, string> StorageModuleNames = new Dictionary<string, string>();
        private readonly Dictionary<string, ItemInfo> StorageModuleItemInfos = new Dictionary<string, ItemInfo>();

        private readonly Dictionary<string, Tuple<object, MethodInfo>> StorageChangeListener = new Dictionary<string, Tuple<object, MethodInfo>>();

        private List<IStorage> Storages = new List<IStorage>();

        struct ItemInfo
        {
            public string StorageName;
            public SubstrateNetApi.Model.Meta.Storage.Type StorageType;
            public SubstrateNetApi.Model.Meta.Storage.Hasher StorageItemKey1Hasher;
            public SubstrateNetApi.Model.Meta.Storage.Hasher StorageItemKey2Hasher;
        }

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
                    ItemInfo itemInfo = new ItemInfo
                    {
                        StorageName = storageItem.Name,
                        StorageType = storageItem.Type,
                        StorageItemKey1Hasher = storageItem.Function.Hasher,
                        StorageItemKey2Hasher = storageItem.Function.Key2Hasher
                    };

                    var key = Utils.Bytes2HexString(RequestGenerator.GetStorageKeyBytesHash(module, storageItem)).ToLower();
                    var moduleNameHash = $"0x{key.Substring(2, 32)}";
                    var storageItemNameHash = $"0x{key.Substring(34, 32)}";

                    if (!StorageModuleNames.ContainsKey(moduleNameHash))
                    {
                        StorageModuleNames.Add(moduleNameHash, module.Name);
                    }

                    if (!StorageModuleItemInfos.ContainsKey(storageItemNameHash))
                    {
                        StorageModuleItemInfos.Add(storageItemNameHash, itemInfo);
                    }
                }
            }

            Log.Information("loaded storage metadata module names {count}", StorageModuleNames.Count);
            Log.Information("loaded storage metadata module item names {count}", StorageModuleItemInfos.Count);
        }

        /// <summary>
        /// This should be moved to the SubstrateNetApi to avoid a dependency on that enum
        /// </summary>
        /// <param name="hasher"></param>
        /// <returns></returns>
        private int StringSizeOfKeyByHasher(SubstrateNetApi.Model.Meta.Storage.Hasher hasher)
        {
            switch (hasher)
            {
                case SubstrateNetApi.Model.Meta.Storage.Hasher.None:
                    return 0;
                case SubstrateNetApi.Model.Meta.Storage.Hasher.BlakeTwo128:
                    return 32;
                case SubstrateNetApi.Model.Meta.Storage.Hasher.BlakeTwo256:
                    return 64;
                case SubstrateNetApi.Model.Meta.Storage.Hasher.BlakeTwo128Concat:
                    return 32;
                case SubstrateNetApi.Model.Meta.Storage.Hasher.Twox128:
                    return 32;
                case SubstrateNetApi.Model.Meta.Storage.Hasher.Twox256:
                    return 64;
                case SubstrateNetApi.Model.Meta.Storage.Hasher.Twox64Concat:
                    return 16;
                case SubstrateNetApi.Model.Meta.Storage.Hasher.Identity:
                    return 0;
                default:
                    return 0;
            }
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

                    if (key.Length < 66)
                    {
                        Log.Debug($"Key of {key.Length} is to small for storage access!");
                        continue;
                    }

                    // [0x][Hash128(ModuleName)][Hash128(StorageName)]
                    var moduleNameHash = $"0x{key.Substring(2, 32)}";
                    var storageItemNameHash = $"0x{key.Substring(34, 32)}";

                    if (!StorageModuleNames.TryGetValue(moduleNameHash, out string moduleName))
                    {
                        Log.Debug($"Unable to find a module with moduleNameHash {moduleNameHash}!");
                        continue;
                    }

                    if (!StorageModuleItemInfos.TryGetValue(storageItemNameHash, out ItemInfo itemInfo))
                    {
                        Log.Debug($"Unable to find a storage with storageItemNameHash {storageItemNameHash}!");
                        continue;
                    }

                    Log.Debug($"OnStorageUpdate {itemInfo.StorageName}!");

                    switch (itemInfo.StorageType)
                    {
                        case SubstrateNetApi.Model.Meta.Storage.Type.Plain:
                            {
                                ProcessStorageChange(moduleName, itemInfo, new string[] { }, change[1]);
                                break;
                            }

                        case SubstrateNetApi.Model.Meta.Storage.Type.Map:
                            {
                                // even not knowing the keysize it's just the left part.
                                var storageItemKeyHash = key.Substring(66);
                                ProcessStorageChange(moduleName, itemInfo, new string[] { storageItemKeyHash }, change[1]);
                                break;
                            }

                        case SubstrateNetApi.Model.Meta.Storage.Type.DoubleMap:
                            {
                                // Currently we can't handle Identity as first Key, as we have no information about the size of the key.
                                var key1Size = StringSizeOfKeyByHasher(itemInfo.StorageItemKey1Hasher);
                                if (key1Size > 0)
                                {
                                    var storageItemKeyHash1 = key.Substring(66, key1Size);
                                    var storageItemKeyHash2 = key.Substring(66 + key1Size);
                                    ProcessStorageChange(moduleName, itemInfo, new string[] { storageItemKeyHash1, storageItemKeyHash2 }, change[1]);
                                }
                                else
                                {
                                    Log.Debug("Not able to decode {type} {name} with hasher {hasher}", itemInfo.StorageType, itemInfo.StorageName, key.Length, 66 + key1Size);
                                }
                                break;
                            }

                        default:
                            Log.Debug("OnStorage update currently doesn't support {type}!", itemInfo.StorageType);
                            break;
                    }
                }
            }
        }

        internal void StartProcessingChanges()
        {
            StorageStartProcessingEvent.Set();
        }

        private void ProcessStorageChange(string moduleName, ItemInfo itemInfo, string[] storageItemKeys, string data)
        {
            var key = $"{moduleName}.{itemInfo.StorageName}";
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
    }
}
