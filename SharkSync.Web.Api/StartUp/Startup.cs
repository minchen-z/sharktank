using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.Tasks;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Cors.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharkSync.Web.Api.Services;
using SharkSync.Web.Api.ViewModels;
using SharkSync.PostgreSQL.Repositories;
using SharkSync.Interfaces;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using SharkSync.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using SharkSync.PostgreSQL.Services;

namespace SharkSync.Web.Api
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IHostingEnvironment env)
        {
            Configuration = configuration;
            Environment = env;
        }

        public static IConfiguration Configuration { get; private set; }

        public static IHostingEnvironment Environment { get; private set; }

        // This method gets called by the runtime. Use this method to add services to the container
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();

            var appSettings = new AppSettings();
            var appSettingSection = Configuration.GetSection(nameof(AppSettings));
            services.Configure<AppSettings>(appSettingSection);
            appSettingSection.Bind(appSettings);
            
            var awsOptions = Configuration.GetAWSOptions();
            var secretManager = awsOptions.CreateServiceClient<IAmazonSecretsManager>();
            var connectionStringSecret = GetSecret<ConnectionStringSecret>(secretManager, appSettings.ConnectionSecretId);

            services.AddEntityFrameworkNpgsql().AddDbContext<DataContext>(options => options.UseNpgsql(connectionStringSecret.GetConnectionString()));

            // Only add CORS if the client is on another domain
            if (appSettings.ClientAppRootUrl != "~")
            {
                services.AddCors(options =>
                {
                    options.AddPolicy("AllowSpecificOrigin",
                        builder => builder
                            .WithOrigins(appSettings.ClientAppRootUrl)
                            .AllowCredentials()
                            .AllowAnyHeader()
                            .AllowAnyMethod());
                });
            }

            services.Configure<MvcOptions>(options =>
            {
                options.Filters.Add(new CorsAuthorizationFilterFactory("AllowSpecificOrigin"));
            });

            services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

            AddDataProtectionOptions(services);

            AddAuthenticationOptions(secretManager, services, appSettings);

            services.AddTransient(typeof(IAccountRepository), typeof(AccountRepository));
            services.AddTransient(typeof(IApplicationRepository), typeof(ApplicationRepository));
            services.AddTransient(typeof(IChangeRepository), typeof(ChangeRepository));

            services.AddScoped(typeof(AuthService));
            services.AddSingleton(typeof(ITimeService), new TimeService());

            services.AddAWSService<IAmazonSecretsManager>();
        }

        private class OAuthSecret
        {
            public string GitHubClientId { get; set; }
            public string GitHubClientSecret { get; set; }
            public string GoogleClientId { get; set; }
            public string GoogleClientSecret { get; set; }
            public string MicrosoftApplicationId { get; set; }
            public string MicrosoftPassword { get; set; }
        }

        private void AddDataProtectionOptions(IServiceCollection services)
        {
            var awsOptions = Configuration.GetAWSOptions();
            var secretManager = awsOptions.CreateServiceClient<IAmazonSecretsManager>();

            services.AddDataProtection();
            services.Configure<KeyManagementOptions>(o =>
            {
                o.XmlRepository = new SecretsManagerXmlRepository(secretManager, "SharkSync-DataProtection");
            });
        }

        private void AddAuthenticationOptions(IAmazonSecretsManager secretManager, IServiceCollection services, AppSettings appSettings)
        {
            OAuthSecret oAuthSecret = GetSecret<OAuthSecret>(secretManager, appSettings.AuthSecretId);

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.SecurePolicy = Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.None;
                options.Cookie.Path = null;
                options.LoginPath = "/Api/Auth/Login";
                options.AccessDeniedPath = "/Api/Auth/AccessDenied";
            })
            .AddOAuth("GitHub", options =>
            {
                options.ClientId = oAuthSecret.GitHubClientId;
                options.ClientSecret = oAuthSecret.GitHubClientSecret;
                options.CallbackPath = new PathString("/signin-github");

                // Include the users email address in the scope
                options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize?scope=user:email";
                options.TokenEndpoint = "https://github.com/login/oauth/access_token";
                options.UserInformationEndpoint = "https://api.github.com/user";

                options.ClaimActions.MapJsonKey(ClaimTypes.PrimarySid, "accountId");

                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = async context =>
                    {
                        JObject user = await RequestUserDetailsFromProvider(context);

                        var accountRepository = context.HttpContext.RequestServices.GetRequiredService<IAccountRepository>();

                        var gitHubId = (string)user["id"];
                        var name = (string)user["name"];
                        var email = (string)user["email"];
                        var avatarUrl = (string)user["avatar_url"];

                        var account = await accountRepository.AddOrGetAsync(name, email, avatarUrl, gitHubId: gitHubId);

                        user["accountId"] = account.Id.ToString();
                        context.RunClaimActions(user);
                    }
                };
            })
            .AddGoogle(options =>
            {
                options.ClientId = oAuthSecret.GoogleClientId;
                options.ClientSecret = oAuthSecret.GoogleClientSecret;

                options.ClaimActions.MapJsonKey(ClaimTypes.PrimarySid, "accountId");

                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = async context =>
                    {
                        JObject user = await RequestUserDetailsFromProvider(context);

                        var accountRepository = context.HttpContext.RequestServices.GetRequiredService<IAccountRepository>();

                        var googleId = (string)user["id"];
                        var name = (string)user["displayName"];
                        var imageObject = (JObject)user["image"];
                        string avatarUrl = null;
                        if (imageObject != null)
                            avatarUrl = (string)imageObject["url"];
                        var emailsObject = (JArray)user["emails"];
                        string email = null;
                        if (emailsObject != null && emailsObject.Any())
                            email = (string)emailsObject.First()["value"];

                        var account = await accountRepository.AddOrGetAsync(name, email, avatarUrl, googleId: googleId);

                        user["accountId"] = account.Id.ToString();
                        context.RunClaimActions(user);
                    }
                };
            })
            .AddMicrosoftAccount(options =>
            {
                options.ClientId = oAuthSecret.MicrosoftApplicationId;
                options.ClientSecret = oAuthSecret.MicrosoftPassword;

                options.ClaimActions.MapJsonKey(ClaimTypes.PrimarySid, "accountId");

                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = async context =>
                    {
                        JObject user = await RequestUserDetailsFromProvider(context);

                        var accountRepository = context.HttpContext.RequestServices.GetRequiredService<IAccountRepository>();

                        var microsoftId = (string)user["id"];
                        var name = (string)user["displayName"];
                        var email = (string)user["userPrincipalName"];

                        // userPrincipalName might not be an email, depending on the account type
                        if (!email.Contains("@"))
                            email = null;

                        var account = await accountRepository.AddOrGetAsync(name, email, null, microsoftId: microsoftId);

                        user["accountId"] = account.Id.ToString();
                        context.RunClaimActions(user);
                    }
                };
            });
        }

        public static T GetSecret<T>(IAmazonSecretsManager secretManager, string secretId)
        {
            var oAuthSecretTask = secretManager.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId });
            oAuthSecretTask.Wait();

            if (oAuthSecretTask.Result == null || string.IsNullOrWhiteSpace(oAuthSecretTask.Result.SecretString))
                throw new Exception($"Missing AWS SecretsManager value for \"{secretId}\" secret");

            var oAuthSecret = JsonConvert.DeserializeObject<T>(oAuthSecretTask.Result.SecretString);
            return oAuthSecret;
        }

        private static async Task<JObject> RequestUserDetailsFromProvider(OAuthCreatingTicketContext context)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);

            var response = await context.Backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();

            var user = JObject.Parse(await response.Content.ReadAsStringAsync());
            return user;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            app.UseHsts(hsts => hsts.MaxAge(days: 365).IncludeSubdomains().Preload());
            app.UseXContentTypeOptions();
            app.UseReferrerPolicy(opts => opts.NoReferrer());
            app.UseCsp(opts => opts
                .DefaultSources(s => s.Self())
                .BlockAllMixedContent()
            );
            app.UseXXssProtection(options => options.EnabledWithBlockMode());
            app.UseXfo(xfo => xfo.Deny());

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler(builder =>
                {
                    builder.Run(async context =>
                    {
                        context.Response.StatusCode = (int)System.Net.HttpStatusCode.InternalServerError;
                        context.Response.ContentType = "application/json";

                        var error = context.Features.Get<IExceptionHandlerFeature>();
                        if (error != null)
                        {
                            var validation = new BaseValidationViewModel();
                            validation.Errors = new[] { error.Error.Message };
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(validation)).ConfigureAwait(false);
                        }
                    });
                });
            }
            
            app.UseAuthentication();

            app.UseMvc();
        }
    }
}
