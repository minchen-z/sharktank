using Amazon.SecretsManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NUnit.Framework;
using SharkSync.PostgreSQL;
using SharkSync.PostgreSQL.Entities;
using SharkSync.Web.Api;
using SharkSync.Web.Api.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SharkSync.IntegrationTests
{
    [TestFixture]
    public class SyncController
    {
        private const string SyncRequestUrl = "https://api.testingallthethings.net/Api/Sync";
        //private const string SyncRequestUrl = "https://localhost:44325/Api/Sync";

        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly IServiceProvider serviceProvider;
        private DataContext db;

        public SyncController()
        {
            var services = new ServiceCollection();
 
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", false)
                .Build();
            services.AddOptions();
            
            var appSettings = new AppSettings();
            var appSettingSection = configuration.GetSection(nameof(AppSettings));
            services.Configure<AppSettings>(appSettingSection);
            appSettingSection.Bind(appSettings);
            
            var awsOptions = configuration.GetAWSOptions();
            var secretManager = awsOptions.CreateServiceClient<IAmazonSecretsManager>();
            var connectionStringSecret = Startup.GetSecret<ConnectionStringSecret>(secretManager, appSettings.ConnectionSecretId);

            services.AddEntityFrameworkNpgsql().AddDbContext<DataContext>(options => options.UseNpgsql(connectionStringSecret.GetConnectionString()));
            serviceProvider = services.BuildServiceProvider();
        }

        [SetUp]
        public void SetUp()
        {
            db = serviceProvider.GetService<DataContext>();
        }

        [Test]
        public async Task SyncController_Post_Fail_Missing_Request()
        {
            var jsonPayload = new StringContent("", Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(SyncRequestUrl, jsonPayload);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(response.Content);

            var syncResponse = JsonConvert.DeserializeObject<SyncResponseViewModel>(await response.Content.ReadAsStringAsync());

            Assert.NotNull(syncResponse);
            Assert.NotNull(syncResponse.Errors);
            Assert.AreEqual(1, syncResponse.Errors.Count());
            Assert.AreEqual("A non-empty request body is required.", syncResponse.Errors.First());
            Assert.False(syncResponse.Success);
        }

        [Test]
        public async Task SyncController_Post_Fail_Empty_Request()
        {
            SyncRequestViewModel request = new SyncRequestViewModel();

            var jsonPayload = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(SyncRequestUrl, jsonPayload);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(response.Content);

            var syncResponse = JsonConvert.DeserializeObject<SyncResponseViewModel>(await response.Content.ReadAsStringAsync());

            Assert.NotNull(syncResponse);
            Assert.NotNull(syncResponse.Errors);
            Assert.AreEqual(1, syncResponse.Errors.Count());
            Assert.AreEqual("app_id missing or invalid request", syncResponse.Errors.First());
            Assert.False(syncResponse.Success);
        }

        [Test]
        public async Task SyncController_Post_Fail_Incorrect_AppId()
        {
            SyncRequestViewModel request = new SyncRequestViewModel()
            {
                AppId = Guid.NewGuid()
            };

            var jsonPayload = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(SyncRequestUrl, jsonPayload);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(response.Content);

            var syncResponse = JsonConvert.DeserializeObject<SyncResponseViewModel>(await response.Content.ReadAsStringAsync());

            Assert.NotNull(syncResponse);
            Assert.NotNull(syncResponse.Errors);
            Assert.AreEqual(1, syncResponse.Errors.Count());
            Assert.AreEqual("No application found for app_id", syncResponse.Errors.First());
            Assert.False(syncResponse.Success);
        }

        [Test]
        public async Task SyncController_Post_Fail_Missing_AppApiAccessKey()
        {
            SyncRequestViewModel request = new SyncRequestViewModel()
            {
                AppId = new Guid("afd8db1e-73b8-4d5f-9cb1-6b49d205555a")
            };

            var jsonPayload = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(SyncRequestUrl, jsonPayload);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(response.Content);

            var syncResponse = JsonConvert.DeserializeObject<SyncResponseViewModel>(await response.Content.ReadAsStringAsync());

            Assert.NotNull(syncResponse);
            Assert.NotNull(syncResponse.Errors);
            Assert.AreEqual(1, syncResponse.Errors.Count());
            Assert.AreEqual("app_api_access_key incorrect for app_id", syncResponse.Errors.First());
            Assert.False(syncResponse.Success);
        }

        [Test]
        public async Task SyncController_Post_Success_Basic_NoChanges_NoGroups()
        {
            var testApp = new Application
            {
                Id = new Guid("afd8db1e-73b8-4d5f-9cb1-6b49d205555a"),
                AccountId = new Guid("250c6f28-4611-4c28-902c-8464fabc510b"),
                AccessKey = new Guid("3d65a27c-9d1d-48a3-a888-89cc0f7851d0"),
                Name = "Integration Test App"
            };

            SyncRequestViewModel request = new SyncRequestViewModel()
            {
                AppId = testApp.Id,
                AppApiAccessKey = testApp.AccessKey
            };

            var jsonPayload = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(SyncRequestUrl, jsonPayload);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(response.Content);

            var syncResponse = JsonConvert.DeserializeObject<SyncResponseViewModel>(await response.Content.ReadAsStringAsync());

            Assert.NotNull(syncResponse);
            Assert.Null(syncResponse.Errors);
            Assert.True(syncResponse.Success);
        }

        [Test]
        public async Task SyncController_Post_Success_Single_Change()
        {
            var testApp = new Application
            {
                Id = new Guid("59eadf1b-c4bf-4ded-8a2b-b80305b960fe"),
                AccountId = new Guid("250c6f28-4611-4c28-902c-8464fabc510b"),
                AccessKey = new Guid("e7b40cf0-2781-4dc7-9545-91fd812fc506"),
                Name = "Integration Test App 2"
            };

            string propertyName = "name";
            string group = "group";
            string entity = "Person";
            Guid recordId = Guid.NewGuid();
            long modifiedMillisecondsAgo = 10000;
            string value = "Neil";

            await DeleteChangeRows(testApp.Id, group);

            SyncRequestViewModel request = new SyncRequestViewModel()
            {
                AppId = testApp.Id,
                AppApiAccessKey = testApp.AccessKey,
                Changes = new List<SyncRequestViewModel.ChangeViewModel>
                {
                    new SyncRequestViewModel.ChangeViewModel
                    {
                        Group = group,
                        RecordId = recordId,
                        Entity = entity,
                        Property = propertyName,
                        MillisecondsAgo = modifiedMillisecondsAgo,
                        Value = value
                    }
                },
                Groups = new List<SyncRequestViewModel.GroupViewModel>
                {
                    new SyncRequestViewModel.GroupViewModel
                    {
                        Group = group,
                        Tidemark = null
                    }
                }
            };

            var jsonPayload = JsonConvert.SerializeObject(request);
            var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(SyncRequestUrl, requestContent);

            var responsePayload = await response.Content?.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Status: {response.StatusCode} Body: {await response.Content.ReadAsStringAsync()}");
            Assert.NotNull(responsePayload);

            var syncResponse = JsonConvert.DeserializeObject<SyncResponseViewModel>(responsePayload);

            Assert.NotNull(syncResponse);
            Assert.Null(syncResponse.Errors);
            Assert.True(syncResponse.Success);
            Assert.NotNull(syncResponse.Groups);
            Assert.AreEqual(1, syncResponse.Groups.Count);

            Assert.NotNull(syncResponse.Groups[0].Changes);
            Assert.AreEqual(1, syncResponse.Groups[0].Changes.Count);

            var dbRows = await GetChangeRows(testApp.Id, group);

            Assert.NotNull(dbRows);
            Assert.AreEqual(1, dbRows.Count);
            Assert.AreEqual(group, dbRows[0].GroupId);
            Assert.AreEqual(propertyName, dbRows[0].Property);
            Assert.AreEqual(recordId, dbRows[0].RecordId);
            Assert.AreEqual(value, dbRows[0].RecordValue);

            Assert.AreEqual(dbRows[0].ClientModified, syncResponse.Groups[0].Changes[0].Modified);
            Assert.AreEqual(propertyName, syncResponse.Groups[0].Changes[0].Property);
            Assert.AreEqual(recordId, syncResponse.Groups[0].Changes[0].RecordId);
            Assert.AreEqual(entity, syncResponse.Groups[0].Changes[0].Entity);
            Assert.AreEqual(value, syncResponse.Groups[0].Changes[0].Value);
        }

        [Test]
        public async Task SyncController_Post_Success_Single_Group_With_Two_Changes()
        {
            var testApp = new Application
            {
                Id = new Guid("b858ceb1-00d0-4427-b45d-e9890b77da36"),
                AccountId = new Guid("250c6f28-4611-4c28-902c-8464fabc510b"),
                AccessKey = new Guid("03172495-6158-44ae-b5b4-6ea5163f02d8"),
                Name = "Integration Test App 3"
            };

            string propertyName = "name";
            string propertyName2 = "age";
            string group = "group";
            string entity = "Person";
            Guid recordId = Guid.NewGuid();
            int modifiedMillisecondsAgo = 10000;
            string value = "Neil";
            string value2 = "10";

            await DeleteChangeRows(testApp.Id, group);

            SyncRequestViewModel request = new SyncRequestViewModel()
            {
                AppId = testApp.Id,
                AppApiAccessKey = testApp.AccessKey,
                Changes = new List<SyncRequestViewModel.ChangeViewModel>
                {
                    new SyncRequestViewModel.ChangeViewModel
                    {
                        Group = group,
                        Entity = entity,
                        Property = propertyName,
                        RecordId = recordId,
                        MillisecondsAgo = modifiedMillisecondsAgo,
                        Value = value
                    },
                    new SyncRequestViewModel.ChangeViewModel
                    {
                        Group = group,
                        Entity = entity,
                        Property = propertyName2,
                        RecordId = recordId,
                        MillisecondsAgo = modifiedMillisecondsAgo,
                        Value = value2
                    }
                }, 
                Groups = new List<SyncRequestViewModel.GroupViewModel>
                {
                    new SyncRequestViewModel.GroupViewModel
                    {
                        Group = group,
                        Tidemark = null
                    }
                }
            };

            var jsonPayload = JsonConvert.SerializeObject(request);
            var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync(SyncRequestUrl, requestContent);

            var responsePayload = await response.Content?.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(responsePayload);

            var syncResponse = JsonConvert.DeserializeObject<SyncResponseViewModel>(responsePayload);

            Assert.NotNull(syncResponse);
            Assert.Null(syncResponse.Errors);
            Assert.True(syncResponse.Success);

            var dbRows = await GetChangeRows(testApp.Id, group);

            Assert.NotNull(dbRows);
            Assert.AreEqual(2, dbRows.Count);
            Assert.AreEqual(group, dbRows[0].GroupId);
            Assert.AreEqual(recordId, dbRows[0].RecordId);
            Assert.AreEqual(entity, dbRows[0].Entity);
            Assert.AreEqual(propertyName, dbRows[0].Property);
            Assert.AreEqual(recordId, dbRows[0].RecordId);
            Assert.AreEqual(value, dbRows[0].RecordValue);

            Assert.AreEqual(group, dbRows[1].GroupId);
            Assert.AreEqual(entity, dbRows[1].Entity);
            Assert.AreEqual(propertyName2, dbRows[1].Property);
            Assert.AreEqual(recordId, dbRows[1].RecordId);
            Assert.AreEqual(value2, dbRows[1].RecordValue);

            Assert.NotNull(syncResponse.Groups);
            Assert.AreEqual(1, syncResponse.Groups.Count);
            Assert.AreEqual(dbRows.Last().Id, syncResponse.Groups[0].Tidemark);
            Assert.AreEqual(group, syncResponse.Groups[0].Group);

            Assert.NotNull(syncResponse.Groups[0].Changes);
            Assert.AreEqual(2, syncResponse.Groups[0].Changes.Count);

            Assert.AreEqual(dbRows[0].ClientModified, syncResponse.Groups[0].Changes[0].Modified);
            Assert.AreEqual(entity, syncResponse.Groups[0].Changes[0].Entity);
            Assert.AreEqual(propertyName, syncResponse.Groups[0].Changes[0].Property);
            Assert.AreEqual(recordId, syncResponse.Groups[0].Changes[0].RecordId);
            Assert.AreEqual(value, syncResponse.Groups[0].Changes[0].Value);

            Assert.AreEqual(dbRows[1].ClientModified, syncResponse.Groups[0].Changes[1].Modified);
            Assert.AreEqual(entity, syncResponse.Groups[0].Changes[1].Entity);
            Assert.AreEqual(propertyName2, syncResponse.Groups[0].Changes[1].Property);
            Assert.AreEqual(recordId, syncResponse.Groups[0].Changes[1].RecordId);
            Assert.AreEqual(value2, syncResponse.Groups[0].Changes[1].Value);
        }

        public async Task<List<Change>> GetChangeRows(Guid appId, string group)
        {
            return await db.Changes.Where(c => c.ApplicationId == appId && c.GroupId == group).ToListAsync();
        }

        public async Task DeleteChangeRows(Guid appId, string group)
        {
            var changes = await db.Changes.Where(c => c.ApplicationId == appId && c.GroupId == group).ToListAsync();

            db.Changes.RemoveRange(changes);

            await db.SaveChangesAsync();
        }
    }
}
