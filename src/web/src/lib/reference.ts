import { useQuery } from "@tanstack/react-query";
import { referenceApi } from "../api/endpoints";
import { queryKeys } from "./queryKeys";

const fiveMinutes = 5 * 60_000;

export function useHubs() {
  return useQuery({ queryKey: queryKeys.hubs, queryFn: ({ signal }) => referenceApi.hubs(signal), staleTime: fiveMinutes });
}

export function useCarriers(enabled: boolean) {
  return useQuery({ queryKey: queryKeys.carriers, queryFn: ({ signal }) => referenceApi.carriers(signal), staleTime: fiveMinutes, enabled });
}
