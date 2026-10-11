-- Çalışma zamanı (uygulama) DB rolü yetkileri — docs/04 §3.14, docs/06 §5 (Karar değişikliği Dilim 1).
-- Migration'ları tabloların sahibi olan ayrı bir rol uygular; uygulama bu dosyadaki yetkilere sahip rolle bağlanır.
-- {{runtime_role}} yer tutucusu, rolü sağlayan kurulum/test adımı tarafından tırnaklı tanımlayıcı ile değiştirilir.
-- audit_entries append-only'dir: çalışma zamanı rolü yalnız INSERT/SELECT yapabilir (UPDATE/DELETE/TRUNCATE yok).

GRANT USAGE ON SCHEMA platform, operations, shipments, identity, tracking TO {{runtime_role}};

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA operations, shipments, identity TO {{runtime_role}};

GRANT SELECT, INSERT, UPDATE, DELETE ON platform.idempotency_records TO {{runtime_role}};
GRANT SELECT, INSERT ON platform.audit_entries TO {{runtime_role}};
-- Outbox (Dilim 7): işlenmiş mesajlar saklama süresi sonunda silinir; bildirim teslim kaydı yalnız eklenir.
GRANT SELECT, INSERT, UPDATE, DELETE ON platform.outbox_messages TO {{runtime_role}};
GRANT SELECT, INSERT ON platform.notification_deliveries TO {{runtime_role}};
GRANT SELECT ON platform.__ef_migrations_history TO {{runtime_role}};

-- tracking (Dilim 6): carrier referans verisi salt okunur; makbuz ve çakışma kanıtı silinmez (saklama süresi sonunda yalnız ham gövde temizlenir).
GRANT SELECT ON tracking.carriers TO {{runtime_role}};
GRANT SELECT, INSERT, UPDATE ON tracking.carrier_event_receipts, tracking.carrier_event_conflicts TO {{runtime_role}};

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA platform, operations, shipments, identity TO {{runtime_role}};
