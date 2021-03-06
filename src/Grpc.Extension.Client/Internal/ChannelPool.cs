﻿using Grpc.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using Grpc.Extension.Abstract.Model;
using Grpc.Extension.Abstract.Discovery;
using Grpc.Extension.Abstract;
using Grpc.Extension.Client.Model;
using Microsoft.Extensions.Options;

namespace Grpc.Extension.Client.Internal
{
    /// <summary>
    /// Channel统一管理
    /// </summary>
    internal class ChannelPool
    {
        private ConcurrentDictionary<string, ChannelInfo> _channels = new ConcurrentDictionary<string, ChannelInfo>();
        private IServiceDiscovery _serviceDiscovery;
        private ILoadBalancer _loadBalancer;
        private IMemoryCache _memoryCache;
        private GrpcClientOptions _grpcClientOptions;

        /// <summary>
        /// Channel统一管理
        /// </summary>
        /// <param name="serviceDiscovery"></param>
        /// <param name="loadBalancer"></param>
        /// <param name="memoryCache"></param>
        /// <param name="grpcClientOptions"></param>
        public ChannelPool(IServiceDiscovery serviceDiscovery, ILoadBalancer loadBalancer,IMemoryCache memoryCache, IOptions<GrpcClientOptions> grpcClientOptions)
        {
            this._serviceDiscovery = serviceDiscovery;
            this._loadBalancer = loadBalancer;
            this._memoryCache = memoryCache;
            this._grpcClientOptions = grpcClientOptions.Value;
        }

        internal static List<ChannelConfig> Configs { get; set; } = new List<ChannelConfig>();

        /// <summary>
        /// 根据客户端代理类型获取channel
        /// </summary>
        public Channel GetChannel(string grpcServiceName)
        {
            var config = Configs?.FirstOrDefault(q => q.GrpcServiceName == grpcServiceName?.Trim());
            if (config == null)
            {
                throw new InternalException(GrpcErrorCode.Internal, $"{grpcServiceName ?? ""} client has not config,please call AddGrpcClient method");
            }
            if (config.UseDirect)
            {
                return GetChannelCore(config.DirectEndpoint,config);
            }
            else//from discovery
            {
                var discoveryUrl = !string.IsNullOrWhiteSpace(config.DiscoveryUrl) ? config.DiscoveryUrl : _grpcClientOptions.DiscoveryUrl;
                var endPoint = GetEndpoint(config.DiscoveryServiceName, discoveryUrl, config.DiscoveryServiceTag);
                return GetChannelCore(endPoint,config);
            }
        }

        /// <summary>
        /// 根据服务名称返回服务地址
        /// </summary>
        private string GetEndpoint(string serviceName, string dicoveryUrl, string serviceTag)
        {
            //获取健康的endpoints
            var isCache = true;
            var healthEndpoints = _memoryCache.GetOrCreate(serviceName, cacheEntry =>
            {
                isCache = false;
                cacheEntry.SetAbsoluteExpiration(TimeSpan.FromSeconds(_grpcClientOptions.ServiceAddressCacheTime));
                return _serviceDiscovery.GetEndpoints(serviceName, dicoveryUrl, serviceTag);
            });
            if (healthEndpoints == null || healthEndpoints.Count == 0)
            {
                throw new InternalException(GrpcErrorCode.Internal,$"get endpoints from discovery of {serviceName} is null");
            }
            //只有重新拉取了健康结点才需要去关闭不健康的Channel
            if (isCache == false) ShutdownErrorChannel(healthEndpoints, serviceName);

            return _loadBalancer.SelectEndpoint(serviceName, healthEndpoints);
        }

        private Channel GetChannelCore(string endpoint,ChannelConfig config)
        {
            //获取channel，不存在就添加
            var channel = _channels.GetOrAdd(endpoint, (key) => CreateChannel(key,config)).Channel;
            //检查channel状态
            if (channel.State != ChannelState.Ready)
            {
                //状态异常就关闭后重建
                channel.ShutdownAsync();
                _channels.TryRemove(config.DiscoveryServiceName, out var tmp);
                //新增或者修改channel
                return _channels.AddOrUpdate(endpoint, (key) => CreateChannel(key, config), (key, value) => CreateChannel(key,config)).Channel;
            }
            else
            {
                return channel;
            }
        }

        private ChannelInfo CreateChannel(string endPoint, ChannelConfig config)
        {
            var channel = new Channel(endPoint, ChannelCredentials.Insecure, config.ChannelOptions);

            var tryCount = 0;//重试计数
            //检查channel状态
            while (channel.State != ChannelState.Ready)
            {
                try
                {
                    channel.ConnectAsync(DateTime.UtcNow.AddSeconds(1)).Wait();
                }
                catch (Exception ex)
                {
                    tryCount++;
                    var exMsg = $"create channel for {config.DiscoveryServiceName} service failed {tryCount},status:{channel.State},endpoint:{endPoint}";
                    var exeption = new InternalException(GrpcErrorCode.Internal, exMsg, ex);
                    if (tryCount > 2)
                    {
                        throw exeption;
                    }
                    else
                    {
                        LoggerAccessor.Instance.OnLoggerError(exeption, LogType.ClientLog);
                    }
                    //重新获取Endpoint,故障转移
                    if (!config.UseDirect)
                    {
                        endPoint = GetEndpoint(config.DiscoveryServiceName, config.DiscoveryUrl, config.DiscoveryServiceTag);
                        channel = new Channel(endPoint, ChannelCredentials.Insecure);
                    }
                }
            }
            return new ChannelInfo() { DiscoveryServiceName= config.DiscoveryServiceName,Channel = channel};
        }

        /// <summary>
        /// 关闭不健康Channel
        /// </summary>
        /// <param name="healthEndpoints"></param>
        /// <param name="serviceName"></param>
        private void ShutdownErrorChannel(List<string> healthEndpoints,string serviceName)
        {
            //获取错误的channel
            var errorChannel = _channels.Where(p => p.Value.DiscoveryServiceName == serviceName &&
                                                !healthEndpoints.Contains(p.Key)).ToList();
            //关闭并删除错误的channel
            foreach (var channel in errorChannel)
            {
                channel.Value.Channel.ShutdownAsync();
                _channels.TryRemove(channel.Key, out var tmp);
            }
        }

        /// <summary>
        /// 关闭所有Channel
        /// </summary>
        public void Shutdown()
        {
            _channels.Select(q => q.Value).ToList().ForEach(q => q.Channel.ShutdownAsync().Wait());
            _channels.Clear();
        }
    }
}
