using SubstrateNetApi;
using System.Threading.Tasks;

namespace JtonNetwork.ServiceLayer.Storage
{
    public interface IStorage
    {
        Task InitializeAsync(SubstrateClient client);
    }
}
