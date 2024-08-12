﻿using MagicVilla_Web.Models.Dto;
using MagicVilla_Utility;
using MagicVilla_Web.Models;
using MagicVilla_Web.Services.IServices;
using Microsoft.AspNetCore.Http.HttpResults;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
using static MagicVilla_Utility.SD;
using AutoMapper.Internal;
using Microsoft.AspNetCore.Authentication;
using System.Runtime.Intrinsics.X86;

namespace MagicVilla_Web.Services
{
    public class BaseService : IBaseService
    {
        public APIResponse responseModel { get; set; }
        private readonly ITokenProvider _tokenProvider;

        // protected: Dit veld is toegankelijk binnen de klasse zelf en in afgeleide klassen.
        protected readonly string VillaApiUrl;
        private IHttpContextAccessor _httpContextAccessor;

        // De HttpClientFactory is onderdeel van DI
        public IHttpClientFactory httpClient { get; set; }

        public BaseService(IHttpClientFactory httpClient, ITokenProvider tokenProvider, IConfiguration configuration
            , IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
			_tokenProvider = tokenProvider;
			this.responseModel = new();
            VillaApiUrl = configuration.GetValue<string>("ServiceUrls:VillaAPI");
            this.httpClient = httpClient;            
        }

        public async Task<T> SendAsync<T>(APIRequest apiRequest, bool withBearer = true)
        {
            try
            {
                var client = httpClient.CreateClient("MagicAPI");

                // We should pass a factory instead of instance of an HttpRequestMessage because
                // it is forbidden to use the same message object more than once. 
                // So if we have to retry request, then we must create a new message object.
                var messageFactory = () =>
                {
                    HttpRequestMessage message = new();
                    if (apiRequest.ContentType == SD.ContentType.MultipartFormData)
                    {
                        message.Headers.Add("Accept", "*/*");
                    }
                    else
                    {
                        message.Headers.Add("Accept", "application/json");
                    }
                    //message.Headers.Add("Accept", "application/json");
                    message.RequestUri = new Uri(apiRequest.Url);

                    if (withBearer && _tokenProvider.GetToken() != null)
                    {
                        var token = _tokenProvider.GetToken();
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                    }

                    if (apiRequest.ContentType == ContentType.MultipartFormData)
                    {
                        var content = new MultipartFormDataContent();

                        foreach (var prop in apiRequest.Data.GetType().GetProperties())
                        {
                            var value = prop.GetValue(apiRequest.Data);
                            if (value is FormFile)
                            {
                                var file = (FormFile)value;
                                if (file != null)
                                {
                                    content.Add(new StreamContent(file.OpenReadStream()), prop.Name, file.FileName);
                                }
                            }
                            else
                            {
                                content.Add(new StringContent(value == null ? "" : value.ToString()), prop.Name);
                            }
                        }
                        message.Content = content;
                    }
                    else
                    {
                        if (apiRequest.Data != null)
                        {
                            // Data will not be null in POST/PUT HTTP calls.
                            message.Content = new StringContent(JsonConvert.SerializeObject(apiRequest.Data),
                                Encoding.UTF8, "application/json");
                        }
                    }

                    // Een API request is een enum, die we vinden in SD, waardoor we een switch condition kunnen maken.
                    switch (apiRequest.ApiType)
                    {
                        case SD.ApiType.POST:
                            message.Method = HttpMethod.Post;
                            break;
                        case SD.ApiType.PUT:
                            message.Method = HttpMethod.Put;
                            break;
                        case SD.ApiType.DELETE:
                            message.Method = HttpMethod.Delete;
                            break;
                        default:
                            message.Method = HttpMethod.Get;
                            break;
                    }
                    return message;
                };

                HttpResponseMessage apiResponse = null;                

                // Bij een error zet hier een breakpoint, zodat je kan zien waar het fout gaat.
                apiResponse = await SendWithRefreshTokenAsync(client, messageFactory, withBearer);

                // This API content will have to deserialize that and once we deserialize it should
                // be the model which is APIResponse. 
                var apiContent = await apiResponse.Content.ReadAsStringAsync();
                try
                {
                    // So we will deserialize that object and we will call the variable as APIResponse. 
                    APIResponse ApiResponse = JsonConvert.DeserializeObject<APIResponse>(apiContent);
                    if (ApiResponse != null && (apiResponse.StatusCode == System.Net.HttpStatusCode.BadRequest
                        || apiResponse.StatusCode == System.Net.HttpStatusCode.NotFound))
                    {
                        ApiResponse.StatusCode = System.Net.HttpStatusCode.BadRequest;
                        ApiResponse.IsSuccess = false;
                        var res = JsonConvert.SerializeObject(ApiResponse);
                        var returnObj = JsonConvert.DeserializeObject<T>(res);
                        return returnObj;
                    }
                }
                catch (Exception e)
                {
                    var exceptionResponse = JsonConvert.DeserializeObject<T>(apiContent);
                    return exceptionResponse;
                }
                var APIResponse = JsonConvert.DeserializeObject<T>(apiContent);
                return APIResponse;
            }
            catch (Exception e)
            {
                var dto = new APIResponse
                {
                    ErrorMessages = new List<string> { Convert.ToString(e.Message) },
                    IsSuccess = false
                };
                var res = JsonConvert.SerializeObject(dto);
                var APIResponse = JsonConvert.DeserializeObject<T>(res);
                return APIResponse;
            }
        }

        private async Task<HttpResponseMessage> SendWithRefreshTokenAsync(HttpClient httpClient, 
            Func<HttpRequestMessage> httpRequestMessageFactory, bool withBearer = true)
        {
            if (!withBearer)
            {
                return await httpClient.SendAsync(httpRequestMessageFactory());
            }
            else
            { 
                TokenDTO tokenDTO = _tokenProvider.GetToken();
                if (tokenDTO != null && !string.IsNullOrEmpty(tokenDTO.AccessToken))
                {   
                    {
                        // API can validate
                        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenDTO.AccessToken);
                    }
                }

                try
                {
                    // We have a new method here, but when we go to the implementation,
                    // if everything is valid, that is good.
                    var response = await httpClient.SendAsync(httpRequestMessageFactory());
                    if (response.IsSuccessStatusCode)
                        return response;

                    // IF this fails then we can pass refresh token!

                    return response;

                }
                catch (Exception e)
                {
                    throw;
                }
            }
        }

        private async Task InvokeRefreshTokenEndPoint(HttpClient httpClient, string existingAccessToken, string existingRefreshToken)
        {
            HttpRequestMessage message = new ();
            message.Headers.Add("Accept", "application/json");
            message.RequestUri = new Uri($"{VillaApiUrl}/api/{SD.CurrentAPIVersion}/UsersAuth/refresh");
            message.Method = HttpMethod.Post;
            message.Content = new StringContent(JsonConvert.SerializeObject(new TokenDTO() 
            { 
                AccessToken = existingAccessToken,
                RefreshToken = existingRefreshToken
            }), Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(message);
            var content = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonConvert.DeserializeObject<APIResponse>(content);

            // Now, if the response is not successful in there, we have the flag API response.
            // That is, success is not true. That means that token refreshment failed, so we cannot
            // use the refresh token anymore. We have to remove that.
            if (apiResponse?.IsSuccess != null)
            {
                // But on top of that we will have to sign out the user as well.
                await _httpContextAccessor.HttpContext.SignOutAsync();
                // We need to clear the token as well.
                _tokenProvider.ClearToken();
            }
            else
            {
                // Everything is valid.
                var tokenDataStr = JsonConvert.SerializeObject(apiResponse.Result);
                var tokenDto = JsonConvert.DeserializeObject<TokenDTO>(tokenDataStr);

                if (tokenDto != null && !string.IsNullOrEmpty(tokenDto.AccessToken))
                {
                    // New method to sign in with the new token that we receive

                    // For the Http client that we have, we have to set the authorization to be the new token.
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenDto.AccessToken);

                }
            }
        }

        private async Task SignInWithNewTokens(TokenDTO tokenDTO)
        {

        }
    }
}
