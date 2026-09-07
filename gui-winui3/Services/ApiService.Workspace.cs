using System;
using System.Net.Http.Json;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public partial class ApiService
    {
        public async Task<WorkspaceConcurrencyResponseDto?> GetWorkspaceConcurrencyAsync(string workspaceId)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<WorkspaceConcurrencyResponseDto>(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/concurrency");
            }
            catch (Exception ex)
            {
                LogApiError("GetWorkspaceConcurrencyAsync", ex);
                return null;
            }
        }

        public async Task<WorkspaceActivityResponseDto?> GetWorkspaceActivityAsync(string workspaceId, long afterSequence = 0, int limit = 50)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<WorkspaceActivityResponseDto>(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/activity?after_sequence={afterSequence}&limit={limit}");
            }
            catch (Exception ex)
            {
                LogApiError("GetWorkspaceActivityAsync", ex);
                return null;
            }
        }

        public async Task<WorkspaceChangesResponseDto?> GetWorkspaceChangesAsync(string workspaceId, long afterSequence = 0, int limit = 50)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<WorkspaceChangesResponseDto>(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/changes?after_sequence={afterSequence}&limit={limit}");
            }
            catch (Exception ex)
            {
                LogApiError("GetWorkspaceChangesAsync", ex);
                return null;
            }
        }

        public async Task<bool> SetWorkspaceConcurrencyAsync(string workspaceId, WorkspaceConcurrencyPolicyDto policy)
        {
            try
            {
                using var response = await _httpClient.PutAsJsonAsync(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/concurrency",
                    policy);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("SetWorkspaceConcurrencyAsync", ex);
                return false;
            }
        }

        public async Task<WorkspaceChangeRestoreResponseDto?> RestoreWorkspaceChangeAsync(
            string workspaceId,
            string changeId,
            string expectedCurrentSha256)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/changes/{Uri.EscapeDataString(changeId)}/restore",
                    new { expected_current_sha256 = expectedCurrentSha256 });
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadFromJsonAsync<WorkspaceChangeRestoreResponseDto>();
            }
            catch (Exception ex)
            {
                LogApiError("RestoreWorkspaceChangeAsync", ex);
                return null;
            }
        }
    }
}
