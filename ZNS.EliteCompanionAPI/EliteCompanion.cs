﻿using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Runtime.Caching;
using System.Text;
using System.Threading.Tasks;
using System.Web.Security;
using NLog;
using ZNS.EliteCompanionAPI.Models;

namespace ZNS.EliteCompanionAPI
{
    public sealed class EliteCompanion
    {
        private static volatile EliteCompanion _Instance;
        private static object syncRoot = new Object();
        private static Logger _Logger = LogManager.GetCurrentClassLogger();
        private static MemoryCache _Cache = MemoryCache.Default;
        private static HttpHelper _Http;
        private static Profile _CurrentProfile;

        private EliteCompanion()
        {
        }

        #region Singleton
        public static EliteCompanion Instance
        {
            get
            {
                if (_Instance == null)
                {
                    lock (syncRoot)
                    {
                        if (_Instance == null)
                            _Instance = new EliteCompanion();
                    }
                }
                return _Instance;
            }
        }
        #endregion

        #region Profiles
        /// <summary>
        /// Creates a user profile, which is stored on disk. Password will be encrypted using machine key. Created profile will be set as active profile.
        /// </summary>
        /// <param name="email">Email of user</param>
        /// <param name="password">Password of user</param>
        public void CreateProfile(string email, string password)
        {
            Profile profile = new Profile();
            profile.Email = email.ToLower();
            //Encrypt password
            var passwordData = Encoding.UTF8.GetBytes(password);
            var encryptedData = MachineKey.Protect(passwordData);
            profile.Password = Convert.ToBase64String(encryptedData);
            profile.Save();
            _Cache.Remove(Constants.CACHE_PROFILEJSON);
            _CurrentProfile = profile;
            _Http = new HttpHelper(_CurrentProfile);
        }

        /// <summary>
        /// Loads a profile and sets it as active.
        /// </summary>
        /// <param name="email">Email of user</param>
        /// <returns>false if profile was not created</returns>
        public bool LoadProfile(string email)
        {
            var profile = Profile.Load(email);
            if (profile == null)
            {
                return false;
            }
            _Cache.Remove(Constants.CACHE_PROFILEJSON);
            _CurrentProfile = profile;
            _Http = new HttpHelper(_CurrentProfile);
            return true;
        }
        #endregion

        #region Login
        /// <summary>
        /// Logins active profile
        /// </summary>
        /// <returns>Login response object</returns>
        public Task<LoginResponse> Login()
        {
            return LoginInternal();
        }

        private async Task<LoginResponse> LoginInternal()
        {
            if (_CurrentProfile.LoggedIn)
            {
                return new LoginResponse {
                    HttpStatusCode = HttpStatusCode.OK,
                    Status = LoginStatus.Ok
                };
            }

            //Get login, to make sure cookies are set
            using (var response = await _Http.Get(Constants.URL_LOGIN, null))
            {
                //If we get redirected to "/" we are already authenticated, due to existing cookie.
                if (response.StatusCode == HttpStatusCode.Found || response.StatusCode == HttpStatusCode.MovedPermanently)
                {
                    if (response.Headers[HttpResponseHeader.Location] == "/")
                    {
                        _CurrentProfile.LoggedIn = true;
                        return new LoginResponse
                        {
                            HttpStatusCode = response.StatusCode,
                            Status = LoginStatus.Ok
                        };
                    }
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                {
                    //Got some http error
                    return new LoginResponse
                    {
                        HttpStatusCode = response.StatusCode,
                        Status = LoginStatus.UnknownError
                    };
                }
            }

            //Post login
            var loginResponse = new LoginResponse();

            //Decrypt password
            var encryptedData = Convert.FromBase64String(_CurrentProfile.Password);
            var unencryptedData = MachineKey.Unprotect(encryptedData);
            var password = Encoding.UTF8.GetString(unencryptedData);

            var data = new NameValueCollection {
                { "email", _CurrentProfile.Email },
                { "password", password }
            };

            using (var response = await _Http.Post(Constants.URL_LOGIN, data, true))
            {
                loginResponse.HttpStatusCode = response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Found || response.StatusCode == HttpStatusCode.MovedPermanently)
                {
                    //Response redirected
                    var redirect = response.Headers[HttpResponseHeader.Location];
                    if (!String.IsNullOrEmpty(redirect))
                    {
                        //Redirect to verification?
                        if (redirect.TrimEnd('/').Equals(Constants.VERIFICATION_REDIRECT_PATH, StringComparison.CurrentCultureIgnoreCase))
                        {
                            loginResponse.Status = LoginStatus.PendingVerification;
                        }
                        //Redirect home?
                        else if (redirect.Equals("/"))
                        {
                            loginResponse.Status = LoginStatus.Ok;
                        }
                    }

                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    loginResponse.Status = LoginStatus.Ok;
                }
                else
                {
                    //Other response code
                    loginResponse.Status = LoginStatus.UnknownError;
                }
            }

            if (loginResponse.Status == LoginStatus.Ok)
            {
                _CurrentProfile.LoggedIn = true;
            }

            return loginResponse;
        }
        #endregion

        #region Verification
        /// <summary>
        /// Use to submit verification code, which is sent as an email to the user on first log in or cookie has expired.
        /// </summary>
        /// <param name="verificationCode">The five character code sent to the user</param>
        /// <returns>Verification response object</returns>
        public Task<VerificationResponse> SubmitVerification(string verificationCode)
        {
            if (String.IsNullOrEmpty(verificationCode))
                throw new ArgumentNullException("verificationCode");
            return SubmitVerificationInternal(verificationCode);
        }

        private async Task<VerificationResponse> SubmitVerificationInternal(string verificationCode)
        {
            var data = new NameValueCollection
            {
                { "code", verificationCode }
            };

            var verificationResponse = new VerificationResponse();
            using (var response = await _Http.Post(Constants.URL_VERIFICATION, data))
            {
                verificationResponse.HttpStatusCode = response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Found || response.StatusCode == HttpStatusCode.MovedPermanently)
                {
                    verificationResponse.Success = true;
                }
                else
                {
                    verificationResponse.Success = false;
                }
            }

            return verificationResponse;
        }
        #endregion

        #region Json Profile
        /// <summary>
        /// Get json profile data for active profile. Will attempt login. It's only possible to get fresh data every 60 seconds
        /// </summary>
        /// <returns>Profile response object</returns>
        public Task<ProfileResponse> GetProfileData()
        {
            return GetProfileDataInternal();
        }

        private async Task<ProfileResponse> GetProfileDataInternal()
        {
            //We don't want to allow hammering of the API, so we cache response for 60 seconds.
            var profileResponse = new ProfileResponse();
            profileResponse.LoginStatus = LoginStatus.Ok;
            string cachedResponse = _Cache.Get(Constants.CACHE_PROFILEJSON) as string;
            if (!String.IsNullOrEmpty(cachedResponse))
            {
                profileResponse.Cached = true;
                profileResponse.HttpStatusCode = HttpStatusCode.OK;
                profileResponse.Json = cachedResponse;
                return profileResponse;
            }

            if (!_CurrentProfile.LoggedIn)
            {
                var loginResponse = await LoginInternal();
                if (loginResponse.Status != LoginStatus.Ok)
                {
                    profileResponse.LoginStatus = loginResponse.Status;
                    profileResponse.HttpStatusCode = loginResponse.HttpStatusCode;
                    profileResponse.Cached = false;
                    return profileResponse;
                }
            }

            using (var response = await _Http.Get(Constants.URL_PROFILE, null))
            {
                profileResponse.Cached = false;
                profileResponse.HttpStatusCode = response.StatusCode;
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using (StreamReader sr = new StreamReader(response.GetResponseStream()))
                    {
                        profileResponse.Json = sr.ReadToEnd();
                    }
                }
            }
            _Cache.Set(Constants.CACHE_PROFILEJSON, profileResponse.Json, DateTimeOffset.Now.AddSeconds(Constants.CACHE_PROFILE_SECONDS));

            return profileResponse;
        }
        #endregion
    }
}