using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using System.Runtime.Serialization;
using System.Reflection;
using System.Net;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Newtonsoft.Json, PublicKey=0024000004800000940000000602000000240000525341310004000001000100F561DF277C6C0B497D629032B410CDCF286E537C054724F7FFA0164345F62B3E642029D7A80CC351918955328C4ADC8A048823EF90B0CF38EA7DB0D729CAF2B633C3BABE08B0310198C1081995C19029BC675193744EAB9D7345B8A67258EC17D112CEBDBBB2A281487DCEEAFB9D83AA930F32103FBE1D2911425BC5744002C7")]

namespace JsonRPC
{
    #region Client-server interaction data contracts
    [DataContract]
    public abstract class Message
    {
        [DataMember(Name = "jsonrpc")]
        public string Version { get; internal protected set; }

        [DataMember(Name = "id")]
        public int Id { get; internal protected set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    [DataContract]
    public sealed class Request<T> : Message
    {
        [DataMember(Name = "method")]
        public string Method;

        [DataMember(Name = "params")]
        public IEnumerable<object> Parameters;

        public Request()
        {
            Version = "2.0";
            Id = GetHashCode();
        }

        public async Task<Response<T>> GetResponseAsync(ServiceMap serviceMap)
        {
            var resString = await serviceMap.MakeRequestAsync(ToString());
            return JsonConvert.DeserializeObject<Response<T>>(resString);
        }
    }

    [DataContract]
    public sealed class Response<T> : Message
    {
        public sealed class Exception : InvalidOperationException
        {
            [DataContract]
            public sealed class Info
            {
                [DataMember(Name = "code")]
                public readonly int Code;

                [DataMember(Name = "message")]
                public readonly string Message;

                [DataMember(Name = "data")]
                public readonly object ExtraData;
            }

            public readonly Info ErrorInfo;

            public override string Message { get { return string.Format("JSON-RPC Error #{0}: {1}", ErrorInfo.Code, ErrorInfo.Message); } }

            public Exception(Info errorInfo)
            {
                ErrorInfo = errorInfo;
            }
        }

        [DataMember(Name = "error")]
        public readonly Exception.Info ErrorInfo;
        public Exception Error { get { return ErrorInfo != null ? new Exception(ErrorInfo) : null; } }

        [DataMember(Name = "result")]
        public readonly T Result;
    }

    [DataContract]
    public class Service
    {
        public ServiceMap ServiceMap { get; internal set; }

        [DataContract]
        public class MethodParam
        {
            [DataMember(Name = "name")]
            public readonly string Name;

            [DataMember(Name = "type")]
            public readonly string Type;

            [DataMember(Name = "optional")]
            public readonly bool IsOptional;

            [DataMember(Name = "default")]
            public readonly object DefaultValue;
        }

        #region Data members
        [DataMember(Name = "transport")]
        internal protected string _transportMethod;
        public string TransportMethod { get { return _transportMethod ?? (this != ServiceMap ? ServiceMap.TransportMethod : null); } }

        [DataMember(Name = "envelope")]
        internal protected string _envelopeType;
        public string EnvelopeType { get { return _envelopeType ?? (this != ServiceMap ? ServiceMap.EnvelopeType : null); } }

        [DataMember(Name = "contentType")]
        internal protected string _contentType;
        public string ContentType { get { return _contentType ?? (this != ServiceMap ? ServiceMap.ContentType : null); } }

        [DataMember(Name = "target")]
        internal protected string _targetUrl;
        public string TargetUrl { get { return _targetUrl ?? (this != ServiceMap ? ServiceMap.TargetUrl : null); } }

        [DataMember(Name = "parameters")]
        public MethodParam[] Parameters;

        [DataMember(Name = "returns")]
        public readonly object ReturnsSchema;

        [DataMember(Name = "name")]
        internal protected string _name;
        public string Name { get { return _name ?? ServiceMap.Services.Single(item => item.Value == this).Key; } }
        #endregion

        public async Task<T> ExecuteAsync<T>(params object[] parameters)
        {
            var response = await new Request<T> { Method = Name, Parameters = parameters }.GetResponseAsync(ServiceMap);
            var error = response.Error;
            if (error != null) throw error;
            return response.Result;
        }
    }
    #endregion

    [DataContract]
    public class ServiceMap : Service
    {
        public new readonly string TargetUrl;

        #region Data members
        [DataMember(Name = "services")]
        public readonly Dictionary<string, Service> Services = new Dictionary<string, Service>();

        [DataMember(Name = "SMDVersion")]
        public readonly string Version;

        [DataMember(Name = "id")]
        public readonly string Id;

        [DataMember(Name = "description")]
        public readonly string Description;
        #endregion

        public event Action<HttpWebRequest> PrepareRequest = request => request.Method = "POST";

        public HttpWebRequest CreateRequest(Uri uri)
        {
            var request = HttpWebRequest.CreateHttp(uri);
            PrepareRequest(request);
            return request;
        }

        public ServiceMap(string targetUrl)
        {
            ServiceMap = this;
            TargetUrl = targetUrl;
        }

        [OnDeserialized]
        internal void OnDeserialized(StreamingContext context)
        {
            if (Services != null)
            {
                foreach (var service in Services.Values)
                {
                    service.ServiceMap = this;
                }
            }
        }

        public async Task<string> MakeRequestAsync(string requestData = "", string relativeUrl = "")
        {
            var request = CreateRequest(new Uri(new Uri(TargetUrl, UriKind.Absolute), relativeUrl));

            using (var reqStream = await request.GetRequestStreamAsync())
            {
                var data = Encoding.UTF8.GetBytes(requestData);
                reqStream.Write(data, 0, data.Length);
            }

            using (var resStream = (await request.GetResponseAsync()).GetResponseStream())
            {
                return await new StreamReader(resStream, Encoding.UTF8).ReadToEndAsync();
            }
        }

        public async Task<ServiceMap> DiscoverAsync(string smdUrl)
        {
            var resString = await MakeRequestAsync(relativeUrl: smdUrl);
            JsonConvert.PopulateObject(resString, this);
            return this;
        }
    }
}
