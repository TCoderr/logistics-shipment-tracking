import type {
  DeliveryFailureReason,
  ExceptionSeverity,
  ExceptionType,
  ResolutionCode,
  Role,
  ScanType,
  ServiceLevel,
  TrackingEventType,
} from "../api/types";

export const roleLabels: Record<Role, string> = {
  OpsAgent: "Operasyon görevlisi",
  OpsManager: "Operasyon yöneticisi",
  HubOperator: "Hub görevlisi",
};

export const serviceLevelLabels: Record<ServiceLevel, string> = { Standard: "Standart", Express: "Ekspres" };

export const eventTypeLabels: Record<TrackingEventType, string> = {
  Created: "Kaydedildi",
  ArrivedAtHub: "Hub'a ulaştı",
  DepartedHub: "Hub'dan çıktı",
  HandedToCarrier: "Carrier'a devredildi",
  CarrierInTransit: "Carrier taşıyor",
  OutForDelivery: "Dağıtıma çıktı",
  DeliveryFailed: "Teslim edilemedi",
  Delivered: "Teslim edildi",
  ReturnInitiated: "Göndericiye iade başladı",
  ReturnedToSender: "Göndericiye iade edildi",
  Cancelled: "İptal edildi",
};

export const eventSourceLabels: Record<string, string> = {
  HubScan: "Hub taraması",
  Operator: "Operatör",
  Carrier: "Carrier",
  System: "Sistem",
};

export const scanTypeLabels: Record<ScanType, string> = {
  Arrival: "Varış",
  Departure: "Çıkış",
  OutForDelivery: "Dağıtıma çıkış",
};

export const failureReasonLabels: Record<DeliveryFailureReason, string> = {
  RecipientAbsent: "Alıcı adreste yok",
  AddressNotFound: "Adres bulunamadı",
  RecipientRefused: "Alıcı reddetti",
  AccessRestricted: "Adrese erişilemedi",
  PackageMissing: "Koli eksik",
  PackageDamaged: "Koli hasarlı",
  Other: "Diğer",
};

export const exceptionTypeLabels: Record<ExceptionType, string> = {
  DeliveryFailed: "Teslim edilemedi",
  MaxAttemptsReached: "Deneme hakkı bitti",
  DeliveryOverdue: "Teslimat gecikti",
  NoMovement: "Hareketsiz gönderi",
  PackageSplit: "Koliler ayrı düştü",
  CarrierEventPendingReview: "Carrier olayı inceleme bekliyor",
  CarrierEventConflict: "Carrier olay çakışması",
};

export const severityLabels: Record<ExceptionSeverity, string> = { Low: "Düşük", Medium: "Orta", High: "Yüksek" };

export const resolutionLabels: Record<ResolutionCode, string> = {
  ReattemptApproved: "Ek teslimat denemesi onayla",
  ReturnToSenderInitiated: "Göndericiye iadeyi başlat",
  CarrierConfirmed: "Carrier ile doğrulandı",
  EventAppliedManually: "Olay elle uygulandı",
  EventDiscarded: "Olay yok sayıldı",
  PackagesConsolidated: "Koliler birleştirildi",
  NoActionNeeded: "İşlem gerekmiyor",
  AutoResolved: "Otomatik çözüldü",
};

// Tarama ve zaman çizelgesi neden kodları (sunucu kodları karışık yazımlıdır; bilinmeyen kod olduğu gibi gösterilir).
const reasonLabels: Record<string, string> = {
  PackageLocationOnly: "Yalnız koli konumu güncellendi",
  MissingDepartureScan: "Önceki hub'dan çıkış taraması eksik",
  LocationOnly: "Yalnız konum bilgisi",
  "technical-duplicate": "Aynı tarama daha önce kaydedildi",
  "business-duplicate": "Aynı olay kısa süre önce kaydedildi",
  "sequence-duplicate": "Aynı sıra numaralı olay daha önce işlendi",
  "package-stale": "Koli için daha yeni bir tarama var",
  "shipment-stale": "Gönderi daha yeni bir durumda",
  "sequence-stale": "Daha yeni sıra numaralı olay işlendi",
  "terminal-state": "Gönderi tamamlanmış durumda",
  "transition-not-allowed": "Bu durumda bu işlem yapılamaz",
  "packages-not-consolidated": "Kolilerin hepsi bu hub'da değil",
  "package-not-at-hub": "Koli bu hub'da görünmüyor",
  "not-destination-hub": "Dağıtıma çıkış yalnız varış hub'ında yapılır",
  "max-attempts-reached": "Teslimat deneme hakkı bitti",
  "future-timestamp": "Olay zamanı gelecekte",
  "too-old": "Olay zamanı çok eski",
  "contradicts-terminal": "Tamamlanmış durumla çelişiyor",
  "carrier-not-assigned": "Gönderi bu carrier'a atanmamış",
  "unknown-shipment": "Gönderi bulunamadı",
  "before-out-for-delivery": "Olay zamanı dağıtıma çıkıştan önce",
  OperatorCancelled: "Operatör iptal etti",
  OperatorReturnToSender: "Operatör iade başlattı",
  OperatorReturnCompleted: "Operatör iadeyi tamamladı",
};

export function reasonLabel(code: string | null | undefined): string | null {
  if (!code) return null;
  return reasonLabels[code] ?? failureReasonLabels[code as DeliveryFailureReason] ?? code;
}

export const carrierOutcomeLabels: Record<string, string> = {
  Applied: "Uygulandı",
  Duplicate: "Tekrar",
  Stale: "Geç geldi (uygulanmadı)",
  Rejected: "Reddedildi",
  PendingReview: "İnceleme bekliyor",
};

export const auditActionLabels: Record<string, string> = {
  ShipmentCreated: "Gönderi oluşturuldu",
  HubScanRecorded: "Hub taraması kaydedildi",
  HubScanRejected: "Hub taraması reddedildi",
  ShipmentCancelled: "Gönderi iptal edildi",
  ReturnToSenderStarted: "Göndericiye iade başlatıldı",
  ReturnCompleted: "İade tamamlandı",
  ExtraAttemptGranted: "Ek deneme hakkı verildi",
  DeliveryAttemptRecorded: "Teslimat denemesi kaydedildi",
  CarrierEventRecorded: "Carrier olayı işlendi",
  CarrierAssigned: "Carrier atandı",
  PublicTrackingCodeRotated: "Takip kodu yenilendi",
  ExceptionOpened: "İstisna açıldı",
  ExceptionAutoResolved: "İstisna otomatik çözüldü",
  ExceptionEscalated: "İstisna önemi yükseltildi",
  ExceptionAssigned: "İstisna atandı",
  ExceptionResolved: "İstisna çözüldü",
};
