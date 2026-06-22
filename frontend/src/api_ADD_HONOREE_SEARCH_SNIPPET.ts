// Add this type near your other exported types in frontend/src/api.ts.

export type HonoreeSearchResult = {
  id: number;
  fullName: string;
  firstName: string;
  middleName?: string | null;
  lastName: string;
  suffix?: string | null;
  nickname?: string | null;
  rank: string;
  kia: boolean;
  serviceBranchName?: string | null;
  flagGrid?: string | null;
  sponsorName?: string | null;
  imageUrl?: string | null;
  pdfUrl?: string | null;
  isActive: boolean;
};

// Add this API object before flagGridApi.

export const honoreeApi = {
  search: (
    instance: IPublicClientApplication,
    account: AccountInfo,
    query: string,
    take = 25
  ) =>
    request<HonoreeSearchResult[]>(
      instance,
      account,
      `/api/honorees/search?q=${encodeURIComponent(query)}&take=${take}`
    )
};
