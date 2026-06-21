namespace PFOH.Api.Dtos;

public record ServiceBranchDto(
    int Id,
    int ServiceBranchCategoryId,
    string ServiceBranchName,
    string Description);

public record ServiceBranchCategoryDto(
    int Id,
    string ServiceBranchCategoryName,
    string Description);
