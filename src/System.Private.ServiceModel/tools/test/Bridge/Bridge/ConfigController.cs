﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Http;
using WcfTestBridgeCommon;

namespace Bridge
{
    public class ConfigController : ApiController
    {
        // Separate events are triggered for changes to specific BridgeConfiguration elements
        public static EventHandler<ChangedEventArgs<string>> ResourceFolderChanged;
        public static EventHandler<ChangedEventArgs<TimeSpan>> IdleTimeoutChanged;

        private static BridgeConfiguration s_bridgeConfiguration = new BridgeConfiguration
        {
            BridgeMaxIdleTimeSpan = IdleTimeoutHandler.Default_MaxIdleTimeSpan
        };

        internal static string s_currentAppDomainName;

        static ConfigController()
        {
            ConfigLock = new object();

            // Register to manage AppDomains in response to changes to the resource folder
            ResourceFolderChanged += (object s, ChangedEventArgs<string> args) =>
            {
                CertificateManager.OnResourceFolderChanged(args.OldValue, args.NewValue);
                CurrentAppDomainName = AppDomainManager.OnResourceFolderChanged(args.OldValue, args.NewValue);
            };
        }

        // We lock the Bridge when necessary to prevent configuration
        // changes or resource instantiation concurrent execution.
        internal static object ConfigLock { get; private set; }

        public static BridgeConfiguration BridgeConfiguration
        {
            get
            {
                return s_bridgeConfiguration;
            }
            set
            {
                s_bridgeConfiguration = value;
            }
        }

        // Gets/sets the friendly name of the AppDomain holding
        // the allocated resources.  Creates the AppDomain on first use.
        public static string CurrentAppDomainName
        {
            get
            {
                lock (ConfigLock)
                {
                    if (s_currentAppDomainName == null)
                    {
                        s_currentAppDomainName = AppDomainManager.CreateAppDomain(ConfigController.BridgeConfiguration.BridgeResourceFolder);
                    }

                    return s_currentAppDomainName;
                }
            }
            set
            {
                s_currentAppDomainName = value;
            }
        }

        public HttpResponseMessage POST(HttpRequestMessage request)
        {
            // A configuration change can have wide impact, so we don't allow concurrent use
            lock (ConfigLock)
            {
                try
                {
                    // Handle deserialization explicitly to bypass MediaTypeFormatter use
                    string nameValuePairs = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Dictionary<string, string> configInfo = JsonSerializer.DeserializeDictionary(nameValuePairs);

                    Trace.WriteLine(String.Format("{0:T} -- POST config received raw content:{1}{2}",
                                                  DateTime.Now, Environment.NewLine, nameValuePairs),
                                    typeof(ConfigController).Name);

                    // Create a new configuration combining the existing one with any provided properties.
                    BridgeConfiguration newConfiguration = new BridgeConfiguration(BridgeConfiguration, configInfo);
                    Trace.WriteLine(String.Format("{0:T} -- applying new config:{0}{1}",
                                                  DateTime.Now, Environment.NewLine, newConfiguration),
                                    typeof(ConfigController).Name);

                    // Take the new configuration and notify listeners of the change.
                    BridgeConfiguration oldConfiguration = BridgeConfiguration;
                    BridgeConfiguration = newConfiguration;

                    // Notify of change of resource folder
                    bool resourceFolderChanged = !String.Equals(oldConfiguration.BridgeResourceFolder, newConfiguration.BridgeResourceFolder, StringComparison.OrdinalIgnoreCase);
                    if (ResourceFolderChanged != null && resourceFolderChanged)
                    {
                        ResourceFolderChanged(this, new ChangedEventArgs<string>(
                                                        oldConfiguration.BridgeResourceFolder,
                                                        newConfiguration.BridgeResourceFolder));
                    }

                    // Notify of change of the idle timeout
                    if (IdleTimeoutChanged != null &&
                        oldConfiguration.BridgeMaxIdleTimeSpan != newConfiguration.BridgeMaxIdleTimeSpan)
                    {
                        IdleTimeoutChanged(this, new ChangedEventArgs<TimeSpan>(
                                                    oldConfiguration.BridgeMaxIdleTimeSpan,
                                                    newConfiguration.BridgeMaxIdleTimeSpan));
                    }

                    // When the resource folder changes, the response is an array of
                    // resource types.  Any other changes returns an empty string.
                    string configResponse = resourceFolderChanged
                                                ? PrepareConfigResponse(TypeCache.Cache[CurrentAppDomainName])
                                                : String.Empty;

                    Trace.WriteLine(String.Format("{0:T} - POST config returning raw content:{1}{2}",
                                                  DateTime.Now, Environment.NewLine, configResponse),
                                    typeof(ConfigController).Name);

                    // Directly return a json string to avoid use of MediaTypeFormatters
                    HttpResponseMessage response = request.CreateResponse(HttpStatusCode.OK);
                    response.Content = new StringContent(configResponse);
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue(JsonSerializer.JsonMediaType);
                    return response;
                }
                catch (Exception ex)
                {
                    var exceptionResponse = ex.Message;
                    Trace.WriteLine(String.Format("{0:T} - POST config exception:{1}{2}",
                                                    DateTime.Now, Environment.NewLine, ex),
                                    typeof(ConfigController).Name);

                    return request.CreateResponse(HttpStatusCode.BadRequest, exceptionResponse);
                }
            }
        }

        public HttpResponseMessage Get(HttpRequestMessage request)
        {
            Dictionary<string, string> dictionary = BridgeConfiguration.ToDictionary();

            string configResponse = JsonSerializer.SerializeDictionary(dictionary);

            Trace.WriteLine(String.Format("{0:T} - GET config returning raw content:{1}{2}",
                                          DateTime.Now, Environment.NewLine, configResponse),
                            typeof(ConfigController).Name);

            // Directly return a json string to avoid use of MediaTypeFormatters
            HttpResponseMessage response = request.CreateResponse(HttpStatusCode.OK);
            response.Content = new StringContent(configResponse);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(JsonSerializer.JsonMediaType);
            return response;
        }

        private static string PrepareConfigResponse(IEnumerable<string> types)
        {
            return string.Format(@"{{
    types : [
        ""{0}""
    ]
}}",
                string.Join("\",\n        \"", types));
        }
    }
}
