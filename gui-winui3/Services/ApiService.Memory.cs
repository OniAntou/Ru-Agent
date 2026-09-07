using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public partial class ApiService
    {
        public async Task<List<MemoryItemDto>?> GetMemoryAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<List<MemoryItemDto>>($"{_baseUrl}/memory");
            }
            catch (Exception ex)
            {
                LogApiError("GetMemoryAsync", ex);
                return null;
            }
        }

        public async Task<bool> ClearMemoryAsync()
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/memory");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ClearMemoryAsync", ex);
                return false;
            }
        }
    }
}
