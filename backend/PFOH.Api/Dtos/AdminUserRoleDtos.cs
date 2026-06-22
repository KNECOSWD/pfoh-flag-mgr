namespace PFOH.Api.Dtos;

public record AdminUserRoleDto(
    string Id,
    string? DisplayName,
    string? Mail,
    string? UserPrincipalName,
    bool IsAdmin);
