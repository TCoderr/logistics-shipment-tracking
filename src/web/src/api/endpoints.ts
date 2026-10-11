import { request, toQuery, type ApiResult } from "./http";
import type {
  AuditPage,
  CarrierReceiptView,
  CarrierReference,
  CreateShipmentRequest,
  CreateShipmentResponse,
  CurrentUser,
  DeliveryAttemptRequest,
  DeliveryAttemptResponse,
  ExceptionFilters,
  ExceptionItem,
  ExceptionQueueResponse,
  HubReference,
  HubScanRequest,
  HubScanResponse,
  OperationsSummary,
  PublicTrackingView,
  ResolutionCode,
  ShipmentCommandResponse,
  ShipmentDetail,
  ShipmentListFilters,
  ShipmentListResponse,
  SimulationRunResponse,
  SimulatorInfo,
} from "./types";

const id = (value: string) => encodeURIComponent(value);

export const authApi = {
  me: (signal?: AbortSignal) => request<CurrentUser>("/api/auth/me", { signal, probe: true }),
  login: (userName: string, password: string) => request<void>("/api/auth/login", { method: "POST", body: { userName, password } }),
  logout: () => request<void>("/api/auth/logout", { method: "POST" }),
};

export const referenceApi = {
  hubs: async (signal?: AbortSignal): Promise<HubReference[]> =>
    (await request<{ items: HubReference[] }>("/api/reference/hubs", { signal })).data.items,
  carriers: async (signal?: AbortSignal): Promise<CarrierReference[]> =>
    (await request<{ items: CarrierReference[] }>("/api/reference/carriers", { signal })).data.items,
};

export const shipmentsApi = {
  list: async (filters: ShipmentListFilters, cursor: string | null, signal?: AbortSignal, limit = 50): Promise<ShipmentListResponse> =>
    (await request<ShipmentListResponse>(`/api/shipments${toQuery({ ...filters, limit, cursor })}`, { signal })).data,

  detail: (shipmentId: string, signal?: AbortSignal): Promise<ApiResult<ShipmentDetail>> =>
    request<ShipmentDetail>(`/api/shipments/${id(shipmentId)}`, { signal }),

  create: (body: CreateShipmentRequest, idempotencyKey: string): Promise<ApiResult<CreateShipmentResponse>> =>
    request<CreateShipmentResponse>("/api/shipments", { method: "POST", body, idempotencyKey }),

  cancel: (shipmentId: string, etag: string, reason: string) =>
    request<ShipmentCommandResponse>(`/api/shipments/${id(shipmentId)}/cancel`, { method: "POST", body: { reason }, ifMatch: etag }),

  returnToSender: (shipmentId: string, etag: string, reason: string) =>
    request<ShipmentCommandResponse>(`/api/shipments/${id(shipmentId)}/return-to-sender`, { method: "POST", body: { reason }, ifMatch: etag }),

  completeReturn: (shipmentId: string, etag: string) =>
    request<ShipmentCommandResponse>(`/api/shipments/${id(shipmentId)}/return-completed`, { method: "POST", ifMatch: etag }),

  assignCarrier: (shipmentId: string, etag: string, carrierCode: string) =>
    request<ShipmentCommandResponse>(`/api/shipments/${id(shipmentId)}/carrier`, { method: "POST", body: { carrierCode }, ifMatch: etag }),

  rotatePublicCode: (shipmentId: string, etag: string) =>
    request<{ shipmentId: string; publicTrackingCode: string }>(`/api/shipments/${id(shipmentId)}/public-tracking-code/rotate`, {
      method: "POST",
      ifMatch: etag,
    }),

  recordDeliveryAttempt: (shipmentId: string, body: DeliveryAttemptRequest, idempotencyKey: string) =>
    request<DeliveryAttemptResponse>(`/api/shipments/${id(shipmentId)}/delivery-attempts`, { method: "POST", body, idempotencyKey }),

  audit: async (shipmentId: string, signal?: AbortSignal): Promise<AuditPage> =>
    (await request<AuditPage>(`/api/shipments/${id(shipmentId)}/audit?limit=100`, { signal })).data,

  carrierReceipts: async (shipmentId: string, signal?: AbortSignal): Promise<CarrierReceiptView[]> =>
    (await request<CarrierReceiptView[]>(`/api/shipments/${id(shipmentId)}/carrier-receipts`, { signal })).data,
};

export const scanApi = {
  scan: (hubCode: string, body: HubScanRequest) => request<HubScanResponse>(`/api/hubs/${id(hubCode)}/scans`, { method: "POST", body }),
};

export const exceptionsApi = {
  list: async (filters: ExceptionFilters, cursor: string | null, signal?: AbortSignal, limit = 50): Promise<ExceptionQueueResponse> =>
    (await request<ExceptionQueueResponse>(`/api/exceptions${toQuery({ ...filters, limit, cursor })}`, { signal })).data,

  assign: (exceptionId: string, etag: string, assigneeUserId: string) =>
    request<ExceptionItem>(`/api/exceptions/${id(exceptionId)}/assign`, { method: "POST", body: { assigneeUserId }, ifMatch: etag }),

  resolve: (exceptionId: string, etag: string, resolutionCode: ResolutionCode, note: string) =>
    request<ExceptionItem>(`/api/exceptions/${id(exceptionId)}/resolve`, { method: "POST", body: { resolutionCode, note }, ifMatch: etag }),
};

export const operationsApi = {
  summary: async (params: { hubCode?: string; date?: string }, signal?: AbortSignal): Promise<OperationsSummary> =>
    (await request<OperationsSummary>(`/api/operations/summary${toQuery(params)}`, { signal })).data,
};

export const publicApi = {
  tracking: async (code: string, signal?: AbortSignal): Promise<PublicTrackingView> =>
    (await request<PublicTrackingView>(`/api/public/tracking/${id(code)}`, { signal, anonymous: true })).data,
};

export const simulatorApi = {
  info: async (signal?: AbortSignal): Promise<SimulatorInfo> => (await request<SimulatorInfo>("/api/dev/carrier-simulator", { signal })).data,
  run: async (trackingNumber: string, scenario: string): Promise<SimulationRunResponse> =>
    (await request<SimulationRunResponse>("/api/dev/carrier-simulator/runs", { method: "POST", body: { trackingNumber, scenario } })).data,
};
