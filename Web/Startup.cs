﻿using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using Twigaten.Web.Parameters;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using CompressedStaticFiles;

namespace Twigaten.Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }
        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            //タグ内の日本語などがエスケープされるのを防ぐ
            //https://qiita.com/rei000/items/67f66fa01b87f720c92f
            services.AddSingleton(HtmlEncoder.Create(UnicodeRanges.All));

            services.AddResponseCompression(options => 
            {
                options.EnableForHttps = true;
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "image/svg+xml", "image/x-icon" });
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
            });
            services.Configure<BrotliCompressionProviderOptions>(options => { options.Level = (CompressionLevel)5; });
            services.Configure<GzipCompressionProviderOptions>(options => { options.Level = CompressionLevel.Fastest; });

            services.Configure<CookiePolicyOptions>(options => { options.CheckConsentNeeded = context => false; });
            services.AddSession();

            services.AddLocalization(options => { options.ResourcesPath = "Locale"; });
            services.AddControllers();
            services.AddRazorPages();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
             if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                //ついでにここで自前Cookieの設定もやる
                LoginParameters.IsDevelopment = true;
            }
            else
            {
                app.UseExceptionHandler("/error");
                //ここで圧縮ファイルを作る
                PreCompress.Proceed(env.WebRootPath).Wait();
            }

            //nginxからの X-Forwarded-For/Proto を受け取る
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });

            app.UseResponseCompression();
            app.UseDefaultFiles();
            app.UseCompressedStaticFiles();

            //Localeを作ってもここに書かないと効かない
            var SupportedCultures = new[] 
            {
                new CultureInfo("ja"),
                new CultureInfo("en")
            };
            //https://stackoverflow.com/questions/43871234/how-to-get-cookiename-used-in-cookierequestcultureprovider
            //cookieの値は"c=en-US|uic=en-US"のようにする(cとuic両方書かないと効かなかった)
            var Localize = app.ApplicationServices.GetService<IOptions<RequestLocalizationOptions>>();
            var cookieProvider = Localize.Value.RequestCultureProviders
                .OfType<CookieRequestCultureProvider>()
                .First();
            cookieProvider.CookieName = "ASP_Locale";
            Localize.Value.DefaultRequestCulture = new RequestCulture("ja");
            Localize.Value.SupportedCultures = SupportedCultures;
            Localize.Value.SupportedUICultures = SupportedCultures;
            app.UseRequestLocalization(Localize.Value);

            app.UseCookiePolicy();
            app.UseSession();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapControllers();
                endpoints.MapFallbackToPage("/error");
            });
        }
    }
}
