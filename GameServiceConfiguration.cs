using JtonNetwork.ServiceLayer.Storage;
using System;
using System.Collections.Generic;
using System.Threading;

namespace JtonNetwork.ServiceLayer
{
    public class GameServiceConfiguration
    {
        public CancellationToken CancellationToken { get; set; }

        public Uri Endpoint { get; set; }

        public List<IStorage> Storages { get; set; }
    }
}
