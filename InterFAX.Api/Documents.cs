using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using InterFAX.Api.Dtos;
using Newtonsoft.Json;

namespace InterFAX.Api
{
    public partial class Documents
    {
        private readonly FaxClient _interfax;
        private const string ResourceUri = "/outbound/documents";
        public const int MaxChunkSize = 256 * 1024; //quarter of a MB

        internal Documents(FaxClient interfax)
        {
            _interfax = interfax;
        }

        private Dictionary<string, string> _supportedMediaTypes;
        public Dictionary<string, string> SupportedMediaTypes
        {
            get
            {
                if (_supportedMediaTypes == null)
                {
                    var assembly = Assembly.GetAssembly(typeof(Documents));
                    var assemblyPath = Path.GetDirectoryName(assembly.Location);
                    var typesFile = Path.Combine(assemblyPath, "SupportedMediaTypes.json");

                    if (!File.Exists(typesFile))
                    {
                        // unpack the types file to the assembly path
                        using (var resource = assembly.GetManifestResourceStream("InterFAX.Api.SupportedMediaTypes.json"))
                        {
                            using (var file = new FileStream(typesFile, FileMode.Create, FileAccess.Write))
                            {
                                resource.CopyTo(file);
                            }
                        }
                    }

                    var mappings = JsonConvert.DeserializeObject<List<MediaTypeMapping>>(File.ReadAllText(typesFile));
                    _supportedMediaTypes = mappings.ToDictionary(
                                mapping => mapping.FileType,
                                mapping => mapping.MediaType);
                }
                return _supportedMediaTypes;
            }
        }

        public IFaxDocument BuildFaxDocument(Uri fileUri)
        {
            return new UriDocument(fileUri);
        }

        public IFaxDocument BuildFaxDocument(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException(filePath);

            var extension = Path.GetExtension(filePath) ?? "*";
            extension = extension.TrimStart('.');

            var mediaType = SupportedMediaTypes.Keys.Contains(extension)
                ? SupportedMediaTypes[extension]
                : "application/octet-stream";

            return new FileDocument(filePath, mediaType);
        }

        /// <summary>
        /// Get a list of previous document upload sessions which are currently available.
        /// </summary>
        /// <param name="listOptions"></param>
        public async Task<IEnumerable<UploadSession>> GetUploadSessions(ListOptions listOptions = null)
        {
            return await _interfax.HttpClient.GetResourceAsync<IEnumerable<UploadSession>>(ResourceUri, listOptions);
        }

        /// <summary>
        /// Create a document upload session.
        /// </summary>
        /// <param name="options"></param>
        /// <returns>The id of the created session.</returns>
        public async Task<string> CreateUploadSession(UploadSessionOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var response = await _interfax.HttpClient.PostAsync(ResourceUri, options);
            return response.Headers.Location.Segments.Last();
        }

        /// <summary>
        /// Get a single upload session object.
        /// </summary>
        /// <param name="sessionId"></param>
        public async Task<UploadSession> GetUploadSession(string sessionId)
        {
            return await _interfax.HttpClient.GetResourceAsync<UploadSession>($"{ResourceUri}/{sessionId}");
        }

        /// <summary>
        /// Uploads a chunk of data to the given document upload session.
        /// </summary>
        /// <param name="sessionId">The id of the upload session.</param>
        /// <param name="offset">The starting position of <paramref name="data"/> in the document.</param>
        /// <param name="data">The data to upload.s</param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> UploadDocumentChunk(string sessionId, long offset, byte[] data)
        {
            if (data.Length > MaxChunkSize)
                throw new ArgumentOutOfRangeException(nameof(data), $"Cannot upload more than {MaxChunkSize} bytes at once.");

            var content = new ByteArrayContent(data);
            var range = new RangeHeaderValue(offset, offset + data.Length - 1);

            return await _interfax.HttpClient.PostRangeAsync($"{ResourceUri}/{sessionId}", content, range);
        }

        /// <summary>
        /// Upload a document to be attached to a fax.
        /// </summary>
        /// <param name="filePath">The full path of the file to be uploaded.</param>
        /// <returns>The upload sessionId of the uploaded document.</returns>
        public UploadSession UploadDocument(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Could not find file : {filePath}", filePath);

            var fileInfo = new FileInfo(filePath);

            var sessionId = CreateUploadSession(new UploadSessionOptions
            {
                Name = fileInfo.Name,
                Size = (int) fileInfo.Length
            }).Result;

            using (var fileStream = File.OpenRead(filePath))
            {
                var buffer = new byte[MaxChunkSize];
                int len;
                while ((len = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var data = new byte[len];
                    Array.Copy(buffer, data, len);
                    var response = UploadDocumentChunk(sessionId, fileStream.Position - len, data).Result;
                    if (response.StatusCode == HttpStatusCode.Accepted) continue;
                    if (response.StatusCode == HttpStatusCode.OK) break;

                    throw new ApiException(response.StatusCode, new Error
                    {
                        Code = (int) response.StatusCode,
                        Message = response.ReasonPhrase,
                        MoreInfo = response.Content.ReadAsStringAsync().Result
                    });
                }
            }

            return GetUploadSession(sessionId).Result;
        }

        /// <summary>
        /// Cancel a document upload session.
        /// </summary>
        /// <param name="sessionId">The identifier of the session to cancel.</param>
        /// <returns>The Uri of the created session.</returns>
        public async Task<string> CancelUploadSession(string sessionId)
        {
            return await _interfax.HttpClient.DeleteResourceAsync($"{ResourceUri}/{sessionId}");
        }
    }
}