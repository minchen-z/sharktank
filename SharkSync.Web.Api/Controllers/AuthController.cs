using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharkSync.Web.Api.Services;
using SharkSync.Web.Api.ViewModels;
using SharkTank.Interfaces.Entities;
using System.Threading.Tasks;

namespace SharkSync.Web.Api.Controllers
{
    public class AuthController : Controller
    {
        ILogger Logger { get; set; }

        AuthService AuthService { get; set; }

        AppSettings AppSettings { get; set; }

        public AuthController(ILogger<AuthController> logger, AuthService authService, IOptions<AppSettings> appSettingsOptions)
        {
            Logger = logger;
            AuthService = authService;
            AppSettings = appSettingsOptions.Value;
        }

        [HttpGet()]
        [Route("Api/Auth/Start")]
        public IActionResult Start(string provider)
        {
            return Challenge(new AuthenticationProperties() { RedirectUri = Url.Action("Complete") }, provider);
        }

        [HttpGet()]
        [Route("Api/Auth/Complete")]
        public IActionResult Complete()
        {
            return Redirect($"{AppSettings.ClientAppRootUrl}/Console/Apps");
        }

        [HttpGet()]
        [Route("Api/Auth/Details")]
        public async Task<IActionResult> Details()
        {
            var loggedInAccount = await AuthService.GetLoggedInAccountAsync(User);
            if (loggedInAccount == null)
                return new UnauthorizedResult();

            var vm = new AuthDetailsViewModel()
            {
                LoggedInUser = new UserDetailsViewModel
                {
                    Id = loggedInAccount.Id,
                    Name = loggedInAccount.Name,
                    EmailAddress = loggedInAccount.EmailAddress,
                    AvatarUrl = loggedInAccount.AvatarUrl
                }
            };

            return ModelState.GetJsonResultWithValidationErrors(vm);
        }

        [HttpGet()]
        [Route("Api/Auth/Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync();

            return Ok();
        }
    }
}
