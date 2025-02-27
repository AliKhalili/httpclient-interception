﻿// Copyright (c) Just Eat, 2017. All rights reserved.
// Licensed under the Apache 2.0 license. See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using JustEat.HttpClientInterception.Matching;

namespace JustEat.HttpClientInterception
{
    /// <summary>
    /// A class containing options for configuring an intercepting <see cref="HttpMessageHandler"/>.
    /// </summary>
    public class HttpClientInterceptorOptions
    {
        /// <summary>
        /// The media type to use for a URL-encoded form.
        /// </summary>
        internal const string FormMediaType = "application/x-www-form-urlencoded";

        /// <summary>
        /// The media type to use for JSON.
        /// </summary>
        internal const string JsonMediaType = "application/json";

        /// <summary>
        /// The <see cref="StringComparer"/> to use for key registrations.
        /// </summary>
        private StringComparer _comparer;

        /// <summary>
        /// The mapped HTTP request interceptors.
        /// </summary>
        private IDictionary<string, HttpInterceptionResponse> _mappings;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpClientInterceptorOptions"/> class.
        /// </summary>
        /// <remarks>
        /// The path and query string of URIs which are registered are case-sensitive.
        /// </remarks>
        public HttpClientInterceptorOptions()
            : this(caseSensitive: true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpClientInterceptorOptions"/> class.
        /// </summary>
        /// <param name="caseSensitive">Whether registered URIs' paths and queries are case-sensitive.</param>
        public HttpClientInterceptorOptions(bool caseSensitive)
        {
            _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            _mappings = new ConcurrentDictionary<string, HttpInterceptionResponse>(_comparer);
        }

        /// <summary>
        /// Gets or sets an optional delegate to invoke when an HTTP request does not match an existing
        /// registration, which optionally returns an <see cref="HttpResponseMessage"/> to use.
        /// </summary>
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnMissingRegistration { get; set; }

        /// <summary>
        /// Gets or sets an optional delegate to invoke when an HTTP request is sent.
        /// </summary>
        public Func<HttpRequestMessage, Task>? OnSend { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to throw an exception if a response has not been registered for an HTTP request.
        /// </summary>
        public bool ThrowOnMissingRegistration { get; set; }

        /// <summary>
        /// Begins a new options scope where any changes to the
        /// currently registered interceptions are only persisted
        /// until the returned <see cref="IDisposable"/> is disposed of.
        /// </summary>
        /// <returns>
        /// An <see cref="IDisposable"/> that is used to end the registration scope.
        /// </returns>
        public IDisposable BeginScope() => new OptionsScope(this);

        /// <summary>
        /// Clears any and all existing registrations.
        /// </summary>
        /// <returns>
        /// The current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        public HttpClientInterceptorOptions Clear()
        {
            _mappings.Clear();
            return this;
        }

        /// <summary>
        /// Clones the existing <see cref="HttpClientInterceptorOptions"/>.
        /// </summary>
        /// <returns>
        /// A new <see cref="HttpClientInterceptorOptions"/> cloned from the current instance.
        /// </returns>
        public HttpClientInterceptorOptions Clone()
        {
            var clone = new HttpClientInterceptorOptions()
            {
                OnMissingRegistration = OnMissingRegistration,
                OnSend = OnSend,
                ThrowOnMissingRegistration = ThrowOnMissingRegistration,
            };

            clone._comparer = _comparer;
            clone._mappings = new ConcurrentDictionary<string, HttpInterceptionResponse>(_mappings, _comparer);

            return clone;
        }

        /// <summary>
        /// Deregisters an existing HTTP request interception, if it exists.
        /// </summary>
        /// <param name="method">The HTTP method to deregister.</param>
        /// <param name="uri">The request URI to deregister.</param>
        /// <returns>
        /// The current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="method"/> or <paramref name="uri"/> is <see langword="null"/>.
        /// </exception>
        public HttpClientInterceptorOptions Deregister(HttpMethod method, Uri uri)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            var interceptor = new HttpInterceptionResponse()
            {
                Method = method,
                RequestUri = uri,
            };

            ConfigureMatcherAndDeregister(interceptor);

            return this;
        }

        /// <summary>
        /// Deregisters an existing HTTP request interception, if it exists.
        /// </summary>
        /// <param name="builder">The HTTP interception to deregister.</param>
        /// <returns>
        /// The current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/> is <see langword="null"/>.
        /// </exception>
        /// <remarks>
        /// If <paramref name="builder"/> has been reconfigured since it was used
        /// to register a previous HTTP request interception it will not remove that
        /// registration. In such cases, use <see cref="Clear"/>.
        /// </remarks>
        public HttpClientInterceptorOptions Deregister(HttpRequestInterceptionBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            HttpInterceptionResponse interceptor = builder.Build();

            ConfigureMatcherAndDeregister(interceptor);

            return this;
        }

        /// <summary>
        /// Registers an HTTP request interception for an array of bytes, replacing any existing registration.
        /// </summary>
        /// <param name="method">The HTTP method to register an interception for.</param>
        /// <param name="uri">The request URI to register an interception for.</param>
        /// <param name="contentFactory">A delegate to a method that returns the raw response content.</param>
        /// <param name="statusCode">The optional HTTP status code to return.</param>
        /// <param name="mediaType">The optional media type for the content-type.</param>
        /// <param name="responseHeaders">The optional HTTP response headers for the response.</param>
        /// <param name="contentHeaders">The optional HTTP response headers for the content.</param>
        /// <param name="onIntercepted">An optional delegate to invoke when the HTTP message is intercepted.</param>
        /// <returns>
        /// The current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="method"/>, <paramref name="uri"/> or <paramref name="contentFactory"/> is <see langword="null"/>.
        /// </exception>
        public HttpClientInterceptorOptions RegisterByteArray(
            HttpMethod method,
            Uri uri,
            Func<Task<byte[]>> contentFactory,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string mediaType = JsonMediaType,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? responseHeaders = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null,
            Func<HttpRequestMessage, Task>? onIntercepted = null)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (contentFactory == null)
            {
                throw new ArgumentNullException(nameof(contentFactory));
            }

            var interceptor = new HttpInterceptionResponse()
            {
                ContentFactory = contentFactory,
                ContentHeaders = contentHeaders,
                ContentMediaType = mediaType,
                Method = method,
                OnIntercepted = DelegateHelpers.ConvertToBooleanTask(onIntercepted),
                RequestUri = uri,
                ResponseHeaders = responseHeaders,
                StatusCode = statusCode,
            };

            ConfigureMatcherAndRegister(interceptor);

            return this;
        }

        /// <summary>
        /// Registers an HTTP request interception for a stream, replacing any existing registration.
        /// </summary>
        /// <param name="method">The HTTP method to register an interception for.</param>
        /// <param name="uri">The request URI to register an interception for.</param>
        /// <param name="contentStream">A delegate to a method that returns the response stream.</param>
        /// <param name="statusCode">The optional HTTP status code to return.</param>
        /// <param name="mediaType">The optional media type for the content-type.</param>
        /// <param name="responseHeaders">The optional HTTP response headers for the response.</param>
        /// <param name="contentHeaders">The optional HTTP response headers for the content.</param>
        /// <param name="onIntercepted">An optional delegate to invoke when the HTTP message is intercepted.</param>
        /// <returns>
        /// The current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="method"/>, <paramref name="uri"/> or <paramref name="contentStream"/> is <see langword="null"/>.
        /// </exception>
        public HttpClientInterceptorOptions RegisterStream(
            HttpMethod method,
            Uri uri,
            Func<Task<Stream>> contentStream,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string mediaType = JsonMediaType,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? responseHeaders = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null,
            Func<HttpRequestMessage, Task>? onIntercepted = null)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (contentStream == null)
            {
                throw new ArgumentNullException(nameof(contentStream));
            }

            var interceptor = new HttpInterceptionResponse()
            {
                ContentStream = contentStream,
                ContentHeaders = contentHeaders,
                ContentMediaType = mediaType,
                Method = method,
                OnIntercepted = DelegateHelpers.ConvertToBooleanTask(onIntercepted),
                RequestUri = uri,
                ResponseHeaders = responseHeaders,
                StatusCode = statusCode,
            };

            ConfigureMatcherAndRegister(interceptor);

            return this;
        }

        /// <summary>
        /// Registers an HTTP request interception, replacing any existing registration.
        /// </summary>
        /// <param name="builder">The <see cref="HttpRequestInterceptionBuilder"/> to use to create the registration.</param>
        /// <returns>
        /// The current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/> is <see langword="null"/>.
        /// </exception>
        public HttpClientInterceptorOptions Register(HttpRequestInterceptionBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            HttpInterceptionResponse interceptor = builder.Build();

            ConfigureMatcherAndRegister(interceptor);

            return this;
        }

        /// <summary>
        /// Gets the HTTP response, if any, set up for the specified HTTP request as an asynchronous operation.
        /// </summary>
        /// <param name="request">The HTTP request to try and get the intercepted response for.</param>
        /// <param name="cancellationToken">The optional token to monitor for cancellation requests.</param>
        /// <returns>
        /// A <see cref="Task{TResult}"/> that returns the HTTP response to use, if any,
        /// for <paramref name="request"/>; otherwise <see langword="null"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="request"/> is <see langword="null"/>.
        /// </exception>
        public virtual async Task<HttpResponseMessage?> GetResponseAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var (found, response) = await TryGetResponseAsync(request).ConfigureAwait(false);

            if (!found)
            {
                return null;
            }

            // If Item1 is true, then Item2 is non-null
            if (response!.OnIntercepted != null && !await response.OnIntercepted(request, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return await BuildResponseAsync(request, response).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a <see cref="DelegatingHandler"/> that uses the interceptors registered for the current instance.
        /// </summary>
        /// <returns>
        /// The <see cref="DelegatingHandler"/> that uses the current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        public virtual DelegatingHandler CreateHttpMessageHandler() => new InterceptingHttpMessageHandler(this);

        /// <summary>
        /// Creates an <see cref="HttpClient"/> that uses the interceptors registered for the current instance.
        /// </summary>
        /// <param name="innerHandler">The optional inner <see cref="HttpMessageHandler"/>.</param>
        /// <returns>
        /// The <see cref="HttpClient"/> that uses the current <see cref="HttpClientInterceptorOptions"/>.
        /// </returns>
        public virtual HttpClient CreateHttpClient(HttpMessageHandler? innerHandler = null)
        {
#pragma warning disable CA2000
            var handler = new InterceptingHttpMessageHandler(this, innerHandler ?? new HttpClientHandler());
#pragma warning restore CA2000

            return new HttpClient(handler, true);
        }

        /// <summary>
        /// Builds the mapping key to use for the specified intercepted HTTP request.
        /// </summary>
        /// <param name="interceptor">The configured HTTP interceptor.</param>
        /// <returns>
        /// A <see cref="string"/> to use as the key for the interceptor registration.
        /// </returns>
        private static string BuildKey(HttpInterceptionResponse interceptor)
        {
            if (interceptor.UserMatcher != null || interceptor.ContentMatcher != null)
            {
                // Use the internal matcher's hash code as UserMatcher (a delegate)
                // will always return the hash code. See https://stackoverflow.com/q/6624151/1064169
                return $"CUSTOM:{interceptor.InternalMatcher!.GetHashCode().ToString(CultureInfo.InvariantCulture)}";
            }

            var builderForKey = new UriBuilder(interceptor.RequestUri!);
            string keyPrefix = string.Empty;

            if (interceptor.IgnoreHost)
            {
                builderForKey.Host = "*";
                keyPrefix = "IGNOREHOST;";
            }

            if (interceptor.IgnorePath)
            {
                builderForKey.Path = string.Empty;
                keyPrefix += "IGNOREPATH;";
            }

            if (interceptor.IgnoreQuery)
            {
                builderForKey.Query = string.Empty;
                keyPrefix += "IGNOREQUERY;";
            }

            if (!interceptor.HasCustomPort)
            {
                builderForKey.Port = -1;
            }

            return $"{keyPrefix};{interceptor.Method!.Method}:{builderForKey}";
        }

        private static void PopulateHeaders(HttpHeaders headers, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? values)
        {
            if (values != null)
            {
                foreach (var pair in values)
                {
                    headers.Add(pair.Key, pair.Value);
                }
            }
        }

        private static async Task<HttpResponseMessage> BuildResponseAsync(HttpRequestMessage request, HttpInterceptionResponse response)
        {
            var result = new HttpResponseMessage(response.StatusCode);

            try
            {
                result.RequestMessage = request;

                if (response.ReasonPhrase != null)
                {
                    result.ReasonPhrase = response.ReasonPhrase;
                }

                if (response.Version != null)
                {
                    result.Version = response.Version;
                }

                if (response.ContentStream != null)
                {
                    result.Content = new StreamContent(await response.ContentStream().ConfigureAwait(false) ?? Stream.Null);
                }
                else
                {
                    byte[] content = await response.ContentFactory!().ConfigureAwait(false) ?? Array.Empty<byte>();
                    result.Content = new ByteArrayContent(content);
                }

                PopulateHeaders(result.Content.Headers, response.ContentHeaders);

                // Do not overwrite a custom Content-Type header if already set
                if (!result.Content.Headers.TryGetValues("content-type", out var contentType))
                {
                    result.Content.Headers.ContentType = new MediaTypeHeaderValue(response.ContentMediaType!);
                }

                PopulateHeaders(result.Headers, response.ResponseHeaders);
            }
            catch (Exception)
            {
                result.Dispose();
                throw;
            }

            return result;
        }

        private async Task<(bool found, HttpInterceptionResponse? response)> TryGetResponseAsync(HttpRequestMessage request)
        {
            var responses = _mappings.Values
                .OrderByDescending((p) => p.Priority.HasValue)
                .ThenBy((p) => p.Priority);

            foreach (var response in responses)
            {
                if (await response.InternalMatcher!.IsMatchAsync(request).ConfigureAwait(false))
                {
                    return new (true, response);
                }
            }

            return new (false, null);
        }

        private void ConfigureMatcher(HttpInterceptionResponse registration)
        {
            RequestMatcher matcher;

            if (registration.UserMatcher != null)
            {
                matcher = new DelegatingMatcher(registration.UserMatcher);
            }
            else
            {
                matcher = new RegistrationMatcher(registration, _comparer);
            }

            registration.InternalMatcher = matcher;
        }

        private void ConfigureMatcherAndRegister(HttpInterceptionResponse registration)
        {
            ConfigureMatcher(registration);

            string key = BuildKey(registration);
            _mappings[key] = registration;
        }

        private void ConfigureMatcherAndDeregister(HttpInterceptionResponse registration)
        {
            ConfigureMatcher(registration);

            string key = BuildKey(registration);
            _mappings.Remove(key);
        }

        private sealed class OptionsScope : IDisposable
        {
            private readonly HttpClientInterceptorOptions _parent;
            private readonly IDictionary<string, HttpInterceptionResponse> _old;
            private readonly IDictionary<string, HttpInterceptionResponse> _new;

            internal OptionsScope(HttpClientInterceptorOptions parent)
            {
                _parent = parent;

                // TODO There might be a nicer way to do this so it's more atomic
                _old = _parent._mappings;
                _parent._mappings = _new = new ConcurrentDictionary<string, HttpInterceptionResponse>(_old, parent._comparer);
            }

            void IDisposable.Dispose()
            {
                Interlocked.CompareExchange(ref _parent._mappings, _old, _new);
                _new.Clear();
            }
        }
    }
}
