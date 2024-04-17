using Hina;
using Hina.Collections;
using Hina.Net;
using Hina.Threading;
using Konseki;
using RtmpSharp.Messaging;
using RtmpSharp.Messaging.Messages;
using RtmpSharp.Net.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RtmpSharp.Net
{
    public partial class RtmpClient
    {
        private const int DefaultPort = 1935;


        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public event EventHandler<ClientDisconnectedException> Disconnected;
        public event EventHandler<Exception> CallbackException;

        // the cancellation source (and token) that this client internally uses to signal disconnection
        private readonly CancellationToken token;
        private readonly CancellationTokenSource source;

        // the serialization context for this rtmp client
        private readonly SerializationContext context;

        // the callback manager that handles completing invocation requests
        private readonly TaskCallbackManager<uint, object> callbacks;

        // fn(message: RtmpMessage, chunk_stream_id: int) -> None
        //     queues a message to be written. this is assigned post-construction by `connectasync`.
        private Action<RtmpMessage, int> queue;

        // the client id that was assigned to us by the remote peer. this is assigned post-construction by
        // `connectasync`, and may be null if no explicit client id was provided.
        private string clientId;

        // counter for monotonically increasing invoke ids
        private int invokeId;

        // true if this connection is no longer connected
        private bool disconnected;

        // a tuple describing the cause of the disconnection. either value may be null.
        private (string message, Exception inner) cause;

        private RtmpClient(SerializationContext context)
        {
            this.context = context;
            callbacks = new TaskCallbackManager<uint, object>();
            source = new CancellationTokenSource();
            token = source.Token;
        }


        #region internal callbacks

        // `internalreceivesubscriptionvalue` will never throw an exception
        private void InternalReceiveSubscriptionValue(string clientId, string subtopic, object body)
        {
            WrapCallback(() => MessageReceived?.Invoke(this, new MessageReceivedEventArgs(clientId, subtopic, body)));
        }

        // called internally by the readers and writers when an error that would invalidate this connection occurs.
        // `inner` may be null.
        private void InternalCloseConnection(string reason, Exception inner)
        {
            Volatile.Write(ref cause.message, reason);
            Volatile.Write(ref cause.inner, inner);
            Volatile.Write(ref disconnected, true);

            source.Cancel();
            callbacks.SetExceptionForAll(DisconnectedException());

            WrapCallback(() => Disconnected?.Invoke(this, DisconnectedException()));
        }

        // this method will never throw an exception unless that exception will be fatal to this connection, and thus
        // the connection would be forced to close.
        private void InternalReceiveEvent(RtmpMessage message)
        {
            switch (message)
            {
                case UserControlMessage u when u.EventType == UserControlMessage.Type.PingRequest:
                    queue(new UserControlMessage(UserControlMessage.Type.PingResponse, u.Values), 2);
                    break;

                case Invoke i:
                    object param = i.Arguments?.FirstOrDefault();

                    switch (i.MethodName)
                    {
                        case "_result":
                            // unwrap the flex wrapper object if it is present
                            AcknowledgeMessage a = param as AcknowledgeMessage;

                            callbacks.SetResult(i.InvokeId, a?.Body ?? param);
                            break;

                        case "_error":
                            // try to unwrap common rtmp and flex error types, if we recognize any.
                            switch (param)
                            {
                                case string v:
                                    callbacks.SetException(i.InvokeId, new Exception(v));
                                    break;

                                case ErrorMessage e:
                                    callbacks.SetException(i.InvokeId, new InvocationException(e, e.FaultCode, e.FaultString, e.FaultDetail, e.RootCause, e.ExtendedData));
                                    break;

                                case AsObject o:
                                    object x;

                                    string code = o.TryGetValue("code", out x) && x is string q ? q : null;
                                    string description = o.TryGetValue("description", out x) && x is string r ? r : null;
                                    string cause = o.TryGetValue("cause", out x) && x is string s ? s : null;

                                    object extended = o.TryGetValue("ex", out x) || o.TryGetValue("extended", out x) ? x : null;

                                    callbacks.SetException(i.InvokeId, new InvocationException(o, code, description, cause, null, extended));
                                    break;

                                default:
                                    callbacks.SetException(i.InvokeId, new InvocationException());
                                    break;
                            }

                            break;

                        case "receive":
                            if (param is AsyncMessage c)
                            {
                                string id = c.ClientId;
                                string value = c.Headers.TryGetValue(AsyncMessageHeaders.Subtopic, out object x) ? x as string : null;
                                object body = c.Body;

                                InternalReceiveSubscriptionValue(id, value, body);
                            }

                            break;

                        case "onstatus":
                            Kon.Trace("received status");
                            break;

                        // [2016-12-26] workaround roslyn compiler bug that would cause the following default cause to
                        // cause a nullreferenceexception on the owning switch statement.
                        //     default:
                        //         Kon.DebugRun(() =>
                        //         {
                        //             Kon.Trace("unknown rtmp invoke method requested", new { method = i.MethodName, args = i.Arguments });
                        //             Debugger.Break();
                        //         });
                        //
                        //         break;

                        default:
                            break;
                    }

                    break;
            }
        }

        #endregion


        #region internal helper methods

        private uint NextInvokeId() => (uint)Interlocked.Increment(ref invokeId);
        private ClientDisconnectedException DisconnectedException() => new ClientDisconnectedException(cause.message, cause.inner);

        // calls a remote endpoint, sent along the specified chunk stream id, on message stream id #0
        private Task<object> InternalCallAsync(Invoke request, int chunkStreamId)
        {
            if (disconnected)
            {
                throw DisconnectedException();
            }

            Task<object> task = callbacks.Create(request.InvokeId);

            queue(request, chunkStreamId);
            return task;
        }

        private void WrapCallback(Action action)
        {
            try
            {
                try { action(); }
                catch (Exception e) { CallbackException?.Invoke(this, e); }
            }
            catch (Exception e)
            {
                Kon.DebugRun(() =>
                {
                    Kon.DebugException("unhandled exception in callback", e);
                    Debugger.Break();
                });
            }
        }

        #endregion


        #region (static) connectasync()

        public class Options
        {
            public string Url;
            public int ChunkLength = 4192;
            public SerializationContext Context;

            // the below fields are optional, and may be null
            public string AppName;
            public string PageUrl;
            public string SwfUrl;

            public string FlashVersion = "WIN 21,0,0,174";

            public object[] Arguments;
            public RemoteCertificateValidationCallback Validate;
        }

        public static async Task<System.Net.Sockets.TcpClient> HandShakeAsync(Options options)
        {
            Check.NotNull(options.Url, options.Context);


            string url = options.Url;
            int chunkLength = options.ChunkLength;
            SerializationContext context = options.Context;
            RemoteCertificateValidationCallback validate = options.Validate ?? ((sender, certificate, chain, errors) => true);

            Uri uri = new Uri(url);
            System.Net.Sockets.TcpClient tcp = await TcpClientEx.ConnectAsync(uri.Host, uri.Port != -1 ? uri.Port : DefaultPort);
            Stream stream = await GetStreamAsync(uri, tcp.GetStream(), validate);

            await Handshake.GoAsync(stream);

            return tcp;
        }
        public static async Task<RtmpClient> ConnectAsync(Options options)
        {
            Check.NotNull(options.Url, options.Context);


            string url = options.Url;
            int chunkLength = options.ChunkLength;
            SerializationContext context = options.Context;
            RemoteCertificateValidationCallback validate = options.Validate ?? ((sender, certificate, chain, errors) => true);

            Uri uri = new Uri(url);
            System.Net.Sockets.TcpClient tcp = await TcpClientEx.ConnectAsync(uri.Host, uri.Port != -1 ? uri.Port : DefaultPort);
            Stream stream = await GetStreamAsync(uri, tcp.GetStream(), validate);

            await Handshake.GoAsync(stream);


            RtmpClient client = new RtmpClient(context);
            Reader reader = new Reader(client, stream, context, client.token);
            Writer writer = new Writer(client, stream, context, client.token);

            reader.RunAsync().Forget();
            writer.RunAsync(chunkLength).Forget();

            client.queue = (message, chunkStreamId) => writer.QueueWrite(message, chunkStreamId);
            client.clientId = await RtmpConnectAsync(
                client: client,
                appName: options.AppName,
                pageUrl: options.PageUrl,
                swfUrl: options.SwfUrl,
                tcUrl: uri.ToString(),
                flashVersion: options.FlashVersion,
                arguments: options.Arguments);


            return client;
        }

        private static async Task<Stream> GetStreamAsync(Uri uri, Stream stream, RemoteCertificateValidationCallback validate)
        {
            CheckDebug.NotNull(uri, stream, validate);

            switch (uri.Scheme)
            {
                case "rtmp":
                    return stream;

                case "rtmps":
                    Check.NotNull(validate);

                    SslStream ssl = new SslStream(stream, false, validate);
                    await ssl.AuthenticateAsClientAsync(uri.Host);

                    return ssl;

                default:
                    throw new ArgumentException($"scheme \"{uri.Scheme}\" must be one of rtmp:// or rtmps://");
            }
        }

        // attempts to perform an rtmp connect, and returns the client id assigned to us (if any - this may be null)
        private static async Task<string> RtmpConnectAsync(RtmpClient client, string appName, string pageUrl, string swfUrl, string tcUrl, string flashVersion, object[] arguments)
        {
            InvokeAmf0 request = new InvokeAmf0
            {
                InvokeId = client.NextInvokeId(),
                MethodName = "connect",
                Arguments = arguments ?? EmptyArray<object>.Instance,
                Headers = new AsObject()
                {
                    { "app",            appName          },
                    { "audioCodecs",    3575             },
                    { "capabilities",   239              },
                    { "flashVer",       flashVersion     },
                    { "fpad",           false            },
                    { "objectEncoding", (double)3        }, // currently hard-coded to amf3
                    { "pageUrl",        pageUrl          },
                    { "swfUrl",         swfUrl           },
                    { "tcUrl",          tcUrl            },
                    { "videoCodecs",    252              },
                    { "videoFunction",  1                },
                },
            };

            IDictionary<string, object> response = await client.InternalCallAsync(request, chunkStreamId: 3) as IDictionary<string, object>;

            return response != null && (response.TryGetValue("clientId", out object clientId) || response.TryGetValue("id", out clientId))
                ? clientId as string
                : null;
        }

        #endregion


        #region rtmpclient methods

        // some servers will fail if `destination` is null (but not if it's an empty string)
        private const string NoDestination = "";

        public async Task<T> InvokeAsync<T>(string method, params object[] arguments)
            => NanoTypeConverter.ConvertTo<T>(
                await InternalCallAsync(new InvokeAmf0() { MethodName = method, Arguments = arguments, InvokeId = NextInvokeId() }, 3));

        public async Task<T> InvokeAsync<T>(string endpoint, string destination, string method, params object[] arguments)
        {
            // this is a flex-style invoke, which *requires* amf3 encoding. fortunately, we always default to amf3
            // decoding and don't have a way to specify amf0 encoding in this iteration of rtmpclient, so no check is
            // needed.

            InvokeAmf3 request = new InvokeAmf3()
            {
                InvokeId = NextInvokeId(),
                MethodName = null,
                Arguments = new[]
                {
                    new RemotingMessage
                    {
                        ClientId    = Guid.NewGuid().ToString("D"),
                        Destination = destination,
                        Operation   = method,
                        Body        = arguments,
                        Headers     = new StaticDictionary<string, object>()
                        {
                            { FlexMessageHeaders.Endpoint,     endpoint },
                            { FlexMessageHeaders.FlexClientId, clientId ?? "nil" }
                        }
                    }
                }
            };

            return NanoTypeConverter.ConvertTo<T>(
                await InternalCallAsync(request, chunkStreamId: 3));
        }

        public async Task<bool> SubscribeAsync(string endpoint, string destination, string subtopic, string clientId)
        {
            Check.NotNull(endpoint, destination, subtopic, clientId);

            CommandMessage message = new CommandMessage
            {
                ClientId = clientId,
                CorrelationId = null,
                Operation = CommandMessage.Operations.Subscribe,
                Destination = destination,
                Headers = new StaticDictionary<string, object>()
                {
                    { FlexMessageHeaders.Endpoint,     endpoint },
                    { FlexMessageHeaders.FlexClientId, clientId },
                    { AsyncMessageHeaders.Subtopic,    subtopic }
                }
            };

            return await InvokeAsync<string>(null, message) == "success";
        }

        public async Task<bool> UnsubscribeAsync(string endpoint, string destination, string subtopic, string clientId)
        {
            Check.NotNull(endpoint, destination, subtopic, clientId);

            CommandMessage message = new CommandMessage
            {
                ClientId = clientId,
                CorrelationId = null,
                Operation = CommandMessage.Operations.Unsubscribe,
                Destination = destination,
                Headers = new KeyDictionary<string, object>()
                {
                    { FlexMessageHeaders.Endpoint,     endpoint },
                    { FlexMessageHeaders.FlexClientId, clientId },
                    { AsyncMessageHeaders.Subtopic,    subtopic }
                }
            };

            return await InvokeAsync<string>(null, message) == "success";
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            Check.NotNull(username, password);

            string credentials = $"{username}:{password}";
            CommandMessage message = new CommandMessage
            {
                ClientId = clientId,
                Destination = NoDestination,
                Operation = CommandMessage.Operations.Login,
                Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)),
            };

            return await InvokeAsync<string>(null, message) == "success";
        }

        public Task LogoutAsync()
        {
            CommandMessage message = new CommandMessage
            {
                ClientId = clientId,
                Destination = NoDestination,
                Operation = CommandMessage.Operations.Logout
            };

            return InvokeAsync<object>(null, message);
        }

        public Task PingAsync()
        {
            CommandMessage message = new CommandMessage
            {
                ClientId = clientId,
                Destination = NoDestination,
                Operation = CommandMessage.Operations.ClientPing
            };

            return InvokeAsync<object>(null, message);
        }

        #endregion


        public Task CloseAsync(bool forced = false)
        {
            // currently we don't have a notion of gracefully closing a connection. all closes are hard force closes,
            // but we leave the possibility for properly implementing graceful closures in the future

            InternalCloseConnection("close-requested-by-user", null);

            return Task.CompletedTask;
        }
    }
}
