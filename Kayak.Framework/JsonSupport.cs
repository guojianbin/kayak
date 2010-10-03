﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using LitJson;
using System.Reflection;
using Kayak.Core;

namespace Kayak.Framework
{
    public static partial class Extensions
    {
        static readonly string MinifiedOutputKey = "JsonSerializerPrettyPrint";

        public static void SetJsonOutputMinified(this IKayakContext context, bool value)
        {
            context.Items.SetJsonOutputMinified(value);
        }

        public static bool GetJsonOutputMinified(this IKayakContext context)
        {
            return context.Items.GetJsonOutputMinified();
        }
        
        public static void SetJsonOutputMinified(this IDictionary<object, object> context, bool value)
        {
            context[MinifiedOutputKey] = value;
        }
        
        public static bool GetJsonOutputMinified(this IDictionary<object, object> context)
        {
            return
                context.ContainsKey(MinifiedOutputKey) &&
                (bool)Convert.ChangeType(context[MinifiedOutputKey], typeof(bool));
        }

        public static IObservable<Unit> SerializeResultToJson(this IKayakContext context, TypedJsonMapper mapper)
        {
            var response = context.Response;

            var info = context.GetInvocationInfo();

            bool minified = context.GetJsonOutputMinified();

            response.Headers["Content-Type"] = minified ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";

            object toSerializer= null;

            if (info.Result != null)
                toSerializer = info.Result;
            else if (info.Exception != null)
            {
                var exception = info.Exception;

                if (exception is TargetInvocationException)
                    exception = exception.InnerException;

                context.Response.SetStatusToInternalServerError();
                toSerializer = new { Error = exception.Message };
            }

            if (toSerializer != null)
            {
                var json = GetJsonRepresentation(toSerializer, mapper, minified);
                response.Headers.SetContentLength(json.Length);
                return context.Response.Write(json);
            }
            else return null;
        }

        public static IHttpServerResponse GetJsonResponse(this InvocationInfo info, TypedJsonMapper jsonMapper, bool minified)
        {
            var response = new BufferedResponse();

            response.Headers["Content-Type"] = minified ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";

            if (info.Result != null)
            {
                response.Add(GetJsonRepresentation(info.Result, jsonMapper, minified));
                response.SetContentLength();
            }
            else if (info.Exception != null)
            {
                var exception = info.Exception;

                if (exception is TargetInvocationException)
                    exception = exception.InnerException;

                response.StatusCode = 503;
                response.ReasonPhrase = "Internal Server Error";
                response.Add(GetJsonRepresentation(new { error = exception.Message }, jsonMapper, minified));
                response.SetContentLength();
            }

            return response;
        }

        static byte[] GetJsonRepresentation(object o, TypedJsonMapper mapper, bool minified)
        {
            var buffer = new MemoryStream();
            var writer = new StreamWriter(buffer);

            mapper.Write(o, new JsonWriter(writer) { Validate = false, PrettyPrint = !minified });
            writer.Flush();

            return buffer.ToArray();
        }

        public static IObservable<Unit> DeserializeArgsFromJson(this IKayakContext context, TypedJsonMapper mapper)
        {
            Func<int, IObservable<LinkedList<ArraySegment<byte>>>> bufferRequestBody = i => BufferRequestBody(() => context.Request.Read(), i).AsCoroutine<LinkedList<ArraySegment<byte>>>();
            return DeserializeArgsFromJsonInternal(context.GetInvocationInfo(), mapper, bufferRequestBody, context.Request.Headers.GetContentLength()).AsCoroutine<Unit>();
        }

        public static IObservable<Unit> DeserializeArgsFromJson(this InvocationInfo info, IHttpServerRequest request, TypedJsonMapper mapper)
        {
            Func<int, IObservable<LinkedList<ArraySegment<byte>>>> bufferRequestBody =
                i => BufferRequestBody(request, i).AsCoroutine<LinkedList<ArraySegment<byte>>>();
            return info.DeserializeArgsFromJsonInternal(mapper, bufferRequestBody, request.Headers.GetContentLength()).AsCoroutine<Unit>();
        }

        //internal static IEnumerable<object> DeserializeArgsFromJsonInternal(this InvocationInfo info, 
        //    TypedJsonMapper mapper, Func<IObservable<ArraySegment<byte>>> read, int contentLength)
        
        internal static IEnumerable<object> DeserializeArgsFromJsonInternal(this InvocationInfo info, 
            TypedJsonMapper mapper, Func<int, IObservable<LinkedList<ArraySegment<byte>>>> bufferRequestBody, int contentLength)
        {
            var parameters = info.Method.GetParameters().Where(p => RequestBodyAttribute.IsDefinedOn(p));

            if (parameters.Count() != 0 && contentLength != 0)
            {
                LinkedList<ArraySegment<byte>> requestBody = null;
                yield return bufferRequestBody(contentLength).Do(d => requestBody = d);

                var reader = new JsonReader(new StringReader(requestBody.GetString()));

                if (parameters.Count() > 1)
                    reader.Read(); // read array start

                foreach (var param in parameters)
                    info.Arguments[param.Position] = mapper.Read(param.ParameterType, reader);

                //if (parameters.Count() > 1)
                //    reader.Read(); // read array end
            }
        }

        // wish i had an evented JSON parser.
        static IEnumerable<object> BufferRequestBody(Func<IObservable<ArraySegment<byte>>> read, int contentLength)
        {
            var bytesRead = 0;
            var result = new LinkedList<ArraySegment<byte>>();

            while (true)
            {
                ArraySegment<byte> data = default(ArraySegment<byte>);
                yield return read().Do(d => data = d);

                result.AddLast(data);

                bytesRead += data.Count;

                if (bytesRead == contentLength)
                    break;
            }

            yield return result;
        }

        static IEnumerable<object> BufferRequestBody(IHttpServerRequest request, int contentLength)
        {
            var totalBytesRead = 0;
            var result = new LinkedList<ArraySegment<byte>>();

            while (true)
            {
                var buffer = new byte[2048];
                var bytesRead = 0;
                yield return request.GetBodyChunk(buffer, 0, buffer.Length).Do(n => bytesRead = n);

                result.AddLast(new ArraySegment<byte>(buffer, 0, bytesRead));
                totalBytesRead += bytesRead;

                if (totalBytesRead == contentLength)
                    break;
            }

            yield return result;
        }
    }
}
