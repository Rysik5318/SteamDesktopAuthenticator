using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamAuth;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// Thrown when Steam no longer accepts the account's session and the user has to log in again.
    /// </summary>
    public class SessionExpiredException : Exception
    {
        public SessionExpiredException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Loads the mobile confirmations list for an account.
    ///
    /// SteamAuth's FetchConfirmationsAsync() checks "success" before "needauth", so when Steam answers
    /// {"success":false,"needauth":true} (the session was revoked server-side while the access token still
    /// looks valid locally) it throws an Exception with a null message, which shows up in the UI as
    /// "Exception of type 'System.Exception' was thrown". This helper handles that case properly:
    /// it forces an access token refresh and retries, and reports a SessionExpiredException if Steam
    /// still rejects the session, so the UI can ask the user to log in again.
    /// </summary>
    public static class ConfirmationsFetcher
    {
        public static async Task<Confirmation[]> FetchAsync(SteamGuardAccount account)
        {
            if (account.Session == null || account.Session.IsRefreshTokenExpired())
                throw new SessionExpiredException("Your Steam session for " + account.AccountName + " has expired.");

            if (account.Session.IsAccessTokenExpired())
                await RefreshAccessTokenAsync(account);

            ConfirmationsListResponse response = await RequestAsync(account);

            if (response.NeedAuthentication)
            {
                // The access token looks valid locally but Steam rejected it. Get a fresh one and retry once.
                await RefreshAccessTokenAsync(account);
                response = await RequestAsync(account);

                if (response.NeedAuthentication)
                    throw new SessionExpiredException("Steam rejected the session for " + account.AccountName + " (it was probably revoked or logged out).");
            }

            if (!response.Success)
            {
                string reason = !string.IsNullOrEmpty(response.Message) ? response.Message : response.Detail;
                if (string.IsNullOrEmpty(reason))
                    reason = "Steam refused to return the confirmations list. Make sure your PC clock is correct, then try again later or log in again (Selected Account > Login again).";
                throw new Exception(reason);
            }

            return response.Confirmations ?? new Confirmation[0];
        }

        private static async Task<ConfirmationsListResponse> RequestAsync(SteamGuardAccount account)
        {
            string url = account.GenerateConfirmationURL();
            string body = await SteamWeb.GETRequest(url, account.Session.GetCookies());

            ConfirmationsListResponse response = null;
            try
            {
                response = JsonConvert.DeserializeObject<ConfirmationsListResponse>(body);
            }
            catch (JsonException)
            {
                // Steam sometimes returns an HTML page (e.g. a login redirect or an error page)
            }

            if (response == null)
                throw new Exception("Steam returned an unexpected response while loading confirmations. Try again later.");

            return response;
        }

        private static async Task RefreshAccessTokenAsync(SteamGuardAccount account)
        {
            string oldAccessToken = account.Session.AccessToken;
            try
            {
                await account.Session.RefreshAccessToken();
            }
            catch (Exception)
            {
                account.Session.AccessToken = oldAccessToken;
                throw new SessionExpiredException("Failed to refresh the Steam session for " + account.AccountName + ".");
            }

            // Steam answers with an empty response when the refresh token was revoked
            if (string.IsNullOrEmpty(account.Session.AccessToken))
            {
                account.Session.AccessToken = oldAccessToken;
                throw new SessionExpiredException("Steam refused to refresh the session for " + account.AccountName + ".");
            }
        }

        private class ConfirmationsListResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }

            [JsonProperty("detail")]
            public string Detail { get; set; }

            [JsonProperty("needauth")]
            public bool NeedAuthentication { get; set; }

            [JsonProperty("conf")]
            public Confirmation[] Confirmations { get; set; }
        }
    }
}
