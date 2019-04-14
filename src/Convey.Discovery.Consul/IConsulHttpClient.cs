using System.Threading.Tasks;

namespace Convey.Discovery.Consul
{
    public interface IConsulHttpClient
    {
        Task<T> GetAsync<T>(string requestUri);
    }
}

