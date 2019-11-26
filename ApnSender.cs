using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Api.PushNotifications
{
    /// <summary>
    /// HTTP2 Apple Push Notification sender
    /// </summary>
    public class ApnSender : IDisposable
    {
        private const bool production = false;
        private const string apnidHeader = "apns-id";

        private readonly string p8privateKey;
        private readonly string p8privateKeyId;
        private readonly string teamId;
        private readonly string appBundleIdentifier;
        private readonly Lazy<string> jwtToken;
        private readonly Lazy<HttpClient> http;
        private readonly Lazy<WinHttpHandler> handler;

        /// <summary>
        /// Initialize sender
        /// </summary>
        /// <param name="p8privateKey">p8 certificate string</param>
        /// <param name="privateKeyId">10 digit p8 certificate id. Usually a part of a downloadable certificate filename</param>
        /// <param name="teamId">Apple 10 digit team id</param>
        /// <param name="appBundleIdentifier">App slug / bundle name</param>
        /// <param name="server">Development or Production server</param>
        public ApnSender(string p8privateKey, string p8privateKeyId, string teamId, string appBundleIdentifier)
        {
            this.p8privateKey = p8privateKey;
            this.p8privateKeyId = p8privateKeyId;
            this.teamId = teamId;
            this.appBundleIdentifier = appBundleIdentifier;
            this.jwtToken = new Lazy<string>(() => CreateJwtToken());
            this.handler = new Lazy<WinHttpHandler>(() => new WinHttpHandler());
            this.http = new Lazy<HttpClient>(() => new HttpClient(handler.Value));
        }

        /// <summary>
        /// Serialize and send notification to APN. Please see how your message should be formatted here:
        /// https://developer.apple.com/library/archive/documentation/NetworkingInternet/Conceptual/RemoteNotificationsPG/CreatingtheNotificationPayload.html#//apple_ref/doc/uid/TP40008194-CH10-SW1
        /// Payload will be serialized using Newtonsoft.Json package.
        /// !IMPORTANT: If you send many messages at once, make sure to retry those calls. Apple typically doesn't like 
        /// to receive too many requests and may ocasionally respond with HTTP 429. Just try/catch this call and retry as needed.
        /// </summary>
        /// <exception cref="HttpRequestException">Throws exception when not successful</exception>
        public async Task SendAsync(
            string json,
            string deviceToken,
            string apnsId = null,
            int apnsExpiration = 0,
            int apnsPriority = 10)
        {
            var path = $"/3/device/{deviceToken}";
			
			var server = production ? "https://api.push.apple.com:443" : "https://api.development.push.apple.com:443";
			
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server + path))
            {
                Version = new Version(2, 0),
                Content = new StringContent(json)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", jwtToken.Value);
            request.Headers.TryAddWithoutValidation(":method", "POST");
            request.Headers.TryAddWithoutValidation(":path", path);
            request.Headers.Add("apns-topic", appBundleIdentifier);
            request.Headers.Add("apns-expiration", apnsExpiration.ToString());
            request.Headers.Add("apns-priority", apnsPriority.ToString());
            if (!string.IsNullOrWhiteSpace(apnsId))
            {
                request.Headers.Add(apnidHeader, apnsId);
            }

            using (var response = await http.Value.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
            }
        }

        public void Dispose()
        {
            if (http.IsValueCreated)
            {
                handler.Value.Dispose();
                http.Value.Dispose();
            }
        }

        private string CreateJwtToken()
        {
            var header = Newtonsoft.Json.JsonConvert.SerializeObject(new { alg = "ES256", kid = p8privateKeyId });
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new { iss = teamId, iat = ToEpoch(DateTime.UtcNow) });

            var key = CngKey.Import(Convert.FromBase64String(p8privateKey), CngKeyBlobFormat.Pkcs8PrivateBlob);
            using (var dsa = new ECDsaCng(key))
            {
                dsa.HashAlgorithm = CngAlgorithm.Sha256;
                var headerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(header));
                var payloadBasae64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
                var unsignedJwtData = $"{headerBase64}.{payloadBasae64}";
                var signature = dsa.SignData(Encoding.UTF8.GetBytes(unsignedJwtData));
                return $"{unsignedJwtData}.{Convert.ToBase64String(signature)}";
            }
        }

        private static int ToEpoch(DateTime time)
        {
            var span = DateTime.UtcNow - new DateTime(1970, 1, 1);
            return Convert.ToInt32(span.TotalSeconds);
        }
    }
}