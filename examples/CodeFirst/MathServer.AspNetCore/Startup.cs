﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Extension.BaseService;
using Grpc.Extensions.AspNetCore;
using Math;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MathServer.AspNetCore
{
    public class Startup
    {
        private IConfiguration _conf;
        public Startup(IConfiguration conf)
        {
            _conf = conf;
        }
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            //添加Grpc扩展
            services.AddGrpcExtensions(_conf);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                //ProtoFirst的Grpc
                endpoints.MapGrpcService<GreeterGrpcImpl>();
                endpoints.MapGet("/", async context =>
                {
                    await context.Response.WriteAsync("Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");
                });
            });
            //CodeFirst的Grpc(会自动扫描TStartup所在程序集下的IGrpcSerivce)
            app.UseGrpcExtensions<MathGrpc>(options =>
            {
                //CodeFirst配制
                options.GlobalPackage = "math";
                options.ProtoNameSpace = "MathGrpc";
            })
            //CodeFirst生成proto
            .UseProtoGenerate("protos", false);
        }
    }
}