// Tel üzerindeki gerçek sözleşme (kaynak koddan çıkarıldı): camelCase, null'lar yazılır, enum'lar PascalCase string, zamanlar UTC.

export type Role = "OpsAgent" | "OpsManager" | "HubOperator";
export type ServiceLevel = "Standard" | "Express";
export type ShipmentStatus =
  | "Created"
  | "AtHub"
  | "InTransit"
  | "OutForDelivery"
  | "DeliveryFailed"
  | "Delivered"
  | "ReturningToSender"
  | "ReturnedToSender"
  | "Cancelled";
export type TrackingEventType =
  | "Created"
  | "ArrivedAtHub"
  | "DepartedHub"
  | "HandedToCarrier"
  | "CarrierInTransit"
  | "OutForDelivery"
  | "DeliveryFailed"
  | "Delivered"
  | "ReturnInitiated"
  | "ReturnedToSender"
  | "Cancelled";
export type EventSource = "HubScan" | "Operator" | "Carrier" | "System";
export type TimelineOutcome = "Applied" | "Stale" | "PendingReview";
export type ScanType = "Arrival" | "Departure" | "OutForDelivery";
export type DeliveryOutcome = "Delivered" | "Failed";
export type DeliveryFailureReason =
  | "RecipientAbsent"
  | "AddressNotFound"
  | "RecipientRefused"
  | "AccessRestricted"
  | "PackageMissing"
  | "PackageDamaged"
  | "Other";
export type ExceptionType =
  | "DeliveryFailed"
  | "MaxAttemptsReached"
  | "DeliveryOverdue"
  | "NoMovement"
  | "PackageSplit"
  | "CarrierEventPendingReview"
  | "CarrierEventConflict";
export type ExceptionSeverity = "Low" | "Medium" | "High";
export type ExceptionStatus = "Open" | "Assigned" | "Resolved";
export type ResolutionCode =
  | "ReattemptApproved"
  | "ReturnToSenderInitiated"
  | "CarrierConfirmed"
  | "EventAppliedManually"
  | "EventDiscarded"
  | "PackagesConsolidated"
  | "NoActionNeeded"
  | "AutoResolved";
export type AllowedAction = "Cancel" | "ReturnToSender" | "CompleteReturn" | "RecordDeliveryAttempt";

export interface CurrentUser {
  id: string;
  displayName: string;
  roles: Role[];
  hubCodes: string[];
  csrfToken: string;
}

export interface HubReference {
  code: string;
  name: string;
  city: string;
  isActive: boolean;
}

export interface CarrierReference {
  code: string;
  name: string;
  isActive: boolean;
}

export interface AddressRequest {
  line1: string;
  line2: string | null;
  district: string | null;
  city: string;
  postalCode: string | null;
  countryCode: string;
}

export interface PartyRequest {
  fullName: string;
  companyName: string | null;
  phone: string;
  email: string | null;
  address: AddressRequest;
}

export interface PackageRequest {
  weightGrams: number;
  lengthMm: number;
  widthMm: number;
  heightMm: number;
  contentDescription: string | null;
}

export interface CreateShipmentRequest {
  serviceLevel: ServiceLevel;
  externalReference: string | null;
  originHubCode: string;
  destinationHubCode: string;
  sender: PartyRequest;
  recipient: PartyRequest;
  packages: PackageRequest[];
  declaredValue: { amount: number; currency: string } | null;
}

export interface CreateShipmentResponse {
  id: string;
  trackingNumber: string;
  publicTrackingCode: string;
  status: ShipmentStatus;
  serviceLevel: ServiceLevel;
  promisedDeliveryDate: string;
  packages: { id: string; sequence: number; labelCode: string }[];
  createdAt: string;
}

export interface ShipmentListItem {
  id: string;
  trackingNumber: string;
  status: ShipmentStatus;
  serviceLevel: ServiceLevel;
  promisedDeliveryDate: string;
  isDelayed: boolean;
  originHubCode: string;
  destinationHubCode: string;
  currentHubCode: string | null;
  carrierCode: string | null;
  recipientCity: string;
  packageCount: number;
  openExceptionCount: number;
  statusChangedAt: string;
  createdAt: string;
}

export interface ShipmentListResponse {
  items: ShipmentListItem[];
  nextCursor: string | null;
  limit: number;
}

export type ShipmentSort =
  | "-createdAt"
  | "createdAt"
  | "promisedDeliveryDate"
  | "-promisedDeliveryDate"
  | "statusChangedAt"
  | "-statusChangedAt";

export interface ShipmentListFilters {
  q?: string;
  status?: ShipmentStatus[];
  hubCode?: string;
  serviceLevel?: ServiceLevel;
  carrierCode?: string;
  delayed?: boolean;
  hasOpenException?: boolean;
  createdFrom?: string;
  createdTo?: string;
  sort?: ShipmentSort;
}

export interface HubSummary {
  code: string;
  name: string;
  city: string;
}

export interface PartyView {
  fullName: string;
  companyName: string | null;
  phone: string | null;
  email: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  district: string | null;
  city: string;
  postalCode: string | null;
  countryCode: string;
}

export interface PackageView {
  id: string;
  sequence: number;
  labelCode: string;
  weightGrams: number;
  dimensionsMm: [number, number, number];
  contentDescription: string | null;
  currentHubCode: string | null;
  lastScanType: ScanType | null;
  lastScannedAt: string | null;
}

export interface TimelineItem {
  id: number;
  eventType: TrackingEventType;
  source: EventSource;
  outcome: TimelineOutcome;
  occurredAt: string;
  receivedAt: string;
  location: { hubCode: string | null; city: string | null } | null;
  statusAfter: ShipmentStatus | null;
  reasonCode: string | null;
  isLate: boolean;
}

export interface OpenExceptionView {
  id: string;
  type: ExceptionType;
  severity: ExceptionSeverity;
  status: "Open" | "Assigned";
  occurrenceCount: number;
  openedAt: string;
  dueAt: string;
  isOverdue: boolean;
  assignedToUserId: string | null;
}

export interface DeliveryAttemptView {
  attemptNumber: number;
  outcome: DeliveryOutcome;
  failureReason: DeliveryFailureReason | null;
  occurredAt: string;
}

export interface ShipmentDetail {
  id: string;
  trackingNumber: string;
  externalReference: string | null;
  kind: string;
  serviceLevel: ServiceLevel;
  status: ShipmentStatus;
  statusOccurredAt: string;
  statusChangedAt: string;
  isDelayed: boolean;
  promisedDeliveryDate: string;
  deliveredAt: string | null;
  originHub: HubSummary;
  destinationHub: HubSummary;
  currentHub: HubSummary | null;
  carrier: { code: string; name: string; handedOverAt: string | null } | null;
  deliveryAttempts: { count: number; max: number; attempts: DeliveryAttemptView[] };
  declaredValue: { amount: number; currency: string } | null;
  sender: PartyView;
  recipient: PartyView;
  packages: PackageView[];
  timeline: TimelineItem[];
  openExceptions: OpenExceptionView[];
  allowedActions: AllowedAction[];
  createdAt: string;
  publicTrackingCode: string | null;
  piiRedacted: boolean;
}

export interface ShipmentCommandResponse {
  id: string;
  trackingNumber: string;
  status: ShipmentStatus;
  statusChangedAt: string;
}

export interface DeliveryAttemptRequest {
  outcome: DeliveryOutcome;
  failureReason: DeliveryFailureReason | null;
  occurredAt: string;
  note: string | null;
}

export interface DeliveryAttemptResponse {
  attemptNumber: number;
  outcome: DeliveryOutcome;
  failureReason: DeliveryFailureReason | null;
  occurredAt: string;
  shipment: ShipmentCommandResponse;
}

export interface HubScanRequest {
  clientScanId: string;
  labelCode: string;
  scanType: ScanType;
  occurredAt: string;
}

export interface ScanViews {
  shipment: { id: string; trackingNumber: string; status: ShipmentStatus; currentHubCode: string | null };
  package: { labelCode: string; currentHubCode: string | null; lastScanType: ScanType | null };
  consolidation: { packagesAtHub: number; packageCount: number };
}

export interface HubScanResponse extends ScanViews {
  outcome: "Applied" | "Duplicate" | "Stale";
  reasonCode: string | null;
}

export interface ExceptionItem {
  id: string;
  type: ExceptionType;
  severity: ExceptionSeverity;
  status: ExceptionStatus;
  shipment: { id: string; trackingNumber: string; status: ShipmentStatus };
  hubCode: string | null;
  occurrenceCount: number;
  openedAt: string;
  dueAt: string;
  isOverdue: boolean;
  assignedTo: { id: string; displayName: string } | null;
  resolvedAt: string | null;
  resolutionCode: ResolutionCode | null;
  allowedResolutions: ResolutionCode[];
  etag: string;
}

export interface ExceptionQueueResponse {
  items: ExceptionItem[];
  nextCursor: string | null;
  limit: number;
}

export interface ExceptionFilters {
  status?: ExceptionStatus[];
  type?: ExceptionType;
  hubCode?: string;
  shipmentId?: string;
  assignedTo?: "me" | "unassigned";
  overdue?: boolean;
  sort?: "dueAt" | "-openedAt";
}

export interface Ratio {
  numerator: number;
  denominator: number;
  rate: number | null;
}

export interface CountByKey {
  key: string;
  count: number;
}

export interface OperationsSummary {
  asOf: string;
  date: string;
  timeZone: string;
  hubCode: string | null;
  dueToday: { total: number; delivered: number };
  delayedOpen: number;
  onTimeDelivery7Days: Ratio;
  firstAttemptDelivery7Days: Ratio;
  openExceptions: { count: number; overdue: number; oldestAgeSeconds: number | null };
  failedAttemptReasonsToday: CountByKey[];
  carrierOutcomes24Hours: CountByKey[];
}

export interface PublicTrackingView {
  trackingNumberMasked: string;
  status: ShipmentStatus;
  statusLabel: string;
  promisedDeliveryDate: string;
  isDelayed: boolean;
  packageCount: number;
  lastUpdatedAt: string;
  destinationCity: string;
  timeline: { occurredAt: string; label: string; city: string | null }[];
}

export interface AuditItem {
  id: number;
  occurredAt: string;
  actorType: string;
  actorId: string;
  actorDisplayName: string | null;
  action: string;
  entityType: string;
  entityId: string;
  outcome: "Succeeded" | "Rejected";
  reasonCode: string | null;
  changes: Record<string, unknown>;
}

export interface AuditPage {
  items: AuditItem[];
  nextBefore: number | null;
}

export interface CarrierReceiptView {
  id: string;
  carrierCode: string;
  externalEventId: string;
  eventType: string | null;
  occurredAt: string | null;
  receivedAt: string;
  processingStatus: string;
  outcome: string | null;
  outcomeReason: string | null;
  attempts: number;
  processedAt: string | null;
  lastError: string | null;
  conflictCount: number;
}

export interface SimulatorInfo {
  carrierCode: string;
  scenarios: string[];
}

export interface SimulationRunResponse {
  runId: string;
  scenario: string;
  deliveries: { eventId: string; eventType: string; httpStatus: number; status: string | null }[];
}
