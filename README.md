# Ogm — Yazdırma İzleme Sistemi

Aynı yerel ağdaki (LAN) Windows bilgisayarlardan **yazıcıya gönderilen işleri** yakalayıp
merkezi bir **panele** aktaran iki uygulamalı bir sistemdir.

| Uygulama | Nerede çalışır | Ne yapar |
|---|---|---|
| **Ogm.Server** | Sizin bilgisayarınız | Ajanlardan gelen işleri toplar, saklar ve web panelinde canlı gösterir |
| **Ogm.Agent** | İzlenen her Windows PC | Yazıcı kuyruğunu izler, çıktı verilerini kaydeder ve sunucuya gönderir; bağlantı yoksa sıraya alır |

---

## 1. Mimari

```
┌──────────────────────────┐          HTTP (LAN)          ┌────────────────────────────┐
│  İzlenen PC (Windows)    │  ────────────────────────►   │  Sizin PC'niz (Windows)    │
│                          │   POST /api/agents/register  │                            │
│  Ogm.Agent (Windows      │   POST /api/agents/heartbeat │  Ogm.Server (Kestrel)      │
│  Servisi, arka planda)   │   POST /api/jobs (multipart) │   - /api/state             │
│                          │                              │   - /api/events (SSE)      │
│  SpoolWatcher            │                              │   - /api/jobs/{id}/payload │
│   └─► OutboxQueue (disk) │                              │   - Web panel (wwwroot)    │
└──────────────────────────┘                              └────────────┬───────────────┘
                                                                       │ SSE (canlı)
                                                                       ▼
                                                                  Tarayıcı paneli
```

- **Taşıma katmanı:** HTTP/REST (basit, güvenlik duvarı dostu).
- **Canlı panel:** Server-Sent Events (SSE) — sunucu paneli anlık olarak iter.
- **Çevrimdışı dayanıklılık:** Ajan, gönderemediği işleri **diskteki kalıcı kuyruğa** yazar;
  bağlantı gelince sırayla gönderir. Veri kaybı olmaz.

---

## 2. Gereksinimler

**Geliştirme / derleme**

- .NET SDK **9.0** veya üzeri (`dotnet --version`)
- Windows 10/11 (ajan için zorunlu; sunucu da Windows'ta test edilmiştir)

**Çalıştırma (yayınlanmış sürüm)**

- Self-contained yayınlandığı için hedef PC'lerde **.NET kurulumu gerekmez**.
- Ajan için **yönetici yetkisi** (servis kurulumu ve spool klasörü okuma).

**Yazıcı tarafı — Tam Otomatik (Kullanıcı Müdahalesi Gerekmez)**

Ajan, Windows yazıcı kuyruğu klasörünü izler:
```
C:\Windows\System32\spool\PRINTERS
```
Normalde Windows, çıktı bittiğinde `.SPL` dosyasını siler. Ancak sistemimiz bu sorunu **tamamen otomatik** çözer:
- **Otomatik Ayar:** Servis kurulurken ve ajan çalıştığında, sistemdeki tüm yazıcılarda *"Yazdırılan belgeleri sakla"* (`KeepPrintedJobs = true`) ayarı otomatik olarak açılır. Manuel olarak yazıcı özelliklerine girmenize **gerek yoktur**.
- **Yeni Yazıcı Takibi:** Bilgisayara sonradan takılan veya eklenen yeni yazıcılar 5 dakikada bir otomatik taranarak saklama ayarı açılır.
- **Otomatik Temizleme (Disk Koruması):** Dosya yakalandıktan ve yazdırma bittikten sonra (`Printed` durumu), tamamlanan işler spooler kuyruğundan otomatik silinir; böylece bilgisayarda çöp dosya birikmez ve disk dolmaz.

---

## 3. Derleme ve duman testi

```cmd
dotnet build Ogm.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
```

Duman testi sunucuyu geçici olarak başlatır; sağlık, panel dosyaları, ajan kaydı,
heartbeat, multipart iş yükleme, durum görüntüsü ve payload indirme adımlarını sınar.

---

## 4. Sunucu (sizin PC'niz)

### Yayınlama ve çalıştırma

```cmd
scripts\server.cmd publish          :: tek dosya yayınlar (dist\server\win-x64)
scripts\server.cmd run              :: yayınlanmış sunucuyu çalıştırır
scripts\server.cmd debug            :: geliştirme modunda çalıştırır
```

Panel: **http://localhost:5099**

### Yapılandırma — `appsettings.json` (exe'nin yanında)

```json
{
  "Server": {
    "ApiKey": "",
    "DataDirectory": "",
    "OnlineThresholdSeconds": 90,
    "HeartbeatSeconds": 30,
    "MaxJobsRetained": 2000
  }
}
```

| Ayar | Açıklama |
|---|---|
| `ApiKey` | Ajanlarla paylaşılan anahtar. **Doldurmanız önerilir**; boşsa kimlik doğrulama kapalıdır |
| `DataDirectory` | Kayıtlar ve yüklenen dosyalar. Boşsa `%ProgramData%\Ogm\Server` |
| `OnlineThresholdSeconds` | Bu süreden uzun süredir heartbeat gelmeyen ajan "çevrimdışı" sayılır |
| `MaxJobsRetained` | Panelde tutulacak en fazla iş kaydı (eskiler silinir) |

> Aynı `ApiKey` değeri **ajanların `appsettings.json` dosyasına** da yazılmalıdır.

### Güvenlik duvarı

Sunucu PC'de **5099** numaralı TCP portuna gelen bağlantılara izin verin:

```cmd
netsh advfirewall firewall add rule name="Ogm Print Server" dir=in action=allow protocol=TCP localport=5099
```

---

## 5. Ajan (izlenen PC'ler)

### Yayınlama ve kurulum

```cmd
:: Ajan paketini hazırla (geliştirme PC'sinde)
src\Ogm.Agent\scripts\agent-service.cmd publish

:: Hedef PC'de, YÖNETİCİ olarak
src\Ogm.Agent\scripts\agent-service.cmd install
```

- `publish` → `dist\agent\win-x64\Ogm.Agent.exe` (tek dosya, .NET gerektirmez)
- `install` → servisi oluşturur, **otomatik başlatmaya** ayarlar ve başlatır
- Servis arka planda çalışır, **görünür pencere yoktur**
- Hata durumunda servis otomatik yeniden başlatılır

Diğer komutlar: `uninstall`, `status`.

### Yapılandırma — `appsettings.json` (exe'nin yanında)

```json
{
  "Agent": {
    "ServerUrl": "http://192.168.1.10:5099",
    "ApiKey": "",
    "SpoolDirectory": "C:\\Windows\\System32\\spool\\PRINTERS",
    "DataDirectory": "",
    "HeartbeatSeconds": 30,
    "UploadRetrySeconds": 15,
    "MaxUploadBatch": 5,
    "MaxUploadAttempts": 50,
    "MaxPayloadBytes": 268435456,
    "CaptureEnabled": true,
    "FileStabilizeMilliseconds": 750
  }
}
```

| Ayar | Açıklama |
|---|---|
| `ServerUrl` | **Sunucu PC'nin LAN IP'si** ve portu (ör. `http://192.168.1.10:5099`) |
| `ApiKey` | Sunucudaki anahtarla **aynı** olmalı |
| `DataDirectory` | Kimlik ve kuyruk klasörü. Boşsa `%ProgramData%\Ogm\Agent` |
| `MaxPayloadBytes` | Bu boyuttan büyük işler kuyruğa alınmaz |
| `FileStabilizeMilliseconds` | Dosyanın yazımının bittiğine karar vermek için beklenen süre |

> Ayar değiştirdikten sonra servisi yeniden başlatın:
> `sc stop OgmPrintAgent && sc start OgmPrintAgent`

### Kuyruk (çevrimdışı davranış)

Sunucu kapalıysa veya ağ yoksa işler **kaybolmaz**:

```
%ProgramData%\Ogm\Agent\
  agent-id.txt          -> bu PC'nin kalıcı kimliği
  outbox\<jobId>\meta.json, payload.bin   -> gönderilmeyi bekleyen işler
  outbox\sent\          -> başarıyla gönderilenler
  outbox\failed\        -> deneme sınırı aşılanlar
```

Bağlantı sağlandığında kuyruk sırayla boşaltılır (`MaxUploadAttempts` aşılırsa iş `failed` klasörüne taşınır).

---

## 6. Panel (Modern Web Dashboard)

- **Gelişmiş Gösterge Paneli:** Koyu (Dark) ve Açık (Light) tema desteği, cam efektli (glassmorphism) kurumsal arayüz
- **Canlı Akış & Sesli Bildirim:** Server-Sent Events (SSE) ile anlık veri akışı; yeni bir iş geldiğinde isteğe bağlı sentezlenmiş sesli bildirim (Web Audio chime) ve toast uyarısı
- **Özet KPI Kartları:** Aktif/toplam ajan oranı çubuğu, toplam iş ve bugünkü iş sayısı, arşivlenen SPL veri hacmi, ajan kuyruk & hata takibi, tespit edilen yazıcı sayısı
- **Görsel İstatistikler & Grafikler:**
  - Son 12 saatlik yazdırma iş yoğunluğu zaman çizelgesi grafiği (SVG)
  - En çok çıktı alan bilgisayarlar & kullanıcılar dağılımı
  - Yazıcı kullanım dağılım çubukları
- **Ajan Yönetimi:** Çevrimiçi/çevrimdışı durum göstergesi (radar animasyonu), makine, kullanıcı, tek tıkla IP kopyalama, işletim sistemi, sürüm, son sinyal ve ajan kuyruk takibi; "İşleri Gör" ile tek tıkla filtreleme
- **Yazdırma İşleri Arşivi & Çoklu Filtreleme:**
  - Canlı arama (belge, makine, yazıcı, kullanıcı, SHA-256)
  - Yazıcıya, bilgisayara, formata (EMF/RAW/TEXT) ve zamana göre filtreleme
  - Çoklu sayfalama (pagination) ve sıralama seçenekleri
  - **CSV ve JSON Dışa Aktarma:** Excel uyumlu UTF-8 BOM destekli CSV ve JSON indirme
- **Dosya İnceleme & Spooler Metin Önizleme Modalı:**
  - Doküman ve yazdırma meta verileri (Job ID, makine, kullanıcı, yazıcı, boyut, SHA-256)
  - **Otomatik Metin Ayıklama:** Spool (.SPL) dosyasının içindeki yazdırılan okunabilir metinlerin (fatura, metin, doküman) ayıklanıp gösterilmesi
  - **Hex Döküm Görüntüleyici:** Başlık baytlarının 16'lık (hex) ve ASCII formatında incelenmesi
  - SPL dosyasını doğrudan tek tıkla indirme
- **Ajan Kurulum Rehberi:** Sunucunun yerel ağdaki (LAN) IP adresini otomatik algılayıp kopyalanabilir tek tıkla kurulum URL'si sunan rehber modalı

---

## 7. HTTP uç noktaları

| Yöntem | Yol | Açıklama |
|---|---|---|
| `POST` | `/api/agents/register` | Ajan kaydı (API anahtarı gerekir) |
| `POST` | `/api/agents/heartbeat` | Ajan durum bildirimi (API anahtarı gerekir) |
| `POST` | `/api/jobs` | Yazdırma işi yükleme — `multipart/form-data`: `meta` (JSON) + `payload` (ikili) |
| `GET` | `/api/state` | Panel için tam durum görüntüsü |
| `GET` | `/api/events` | Canlı akış (Server-Sent Events) |
| `GET` | `/api/jobs/{jobId}/payload` | İşin ham verisi (.spl) |
| `GET` | `/api/jobs/{jobId}/preview` | İşin dosya analizi, çıkarılan metin ve hex dökümü |
| `GET` | `/api/server/info` | Sunucu sistem bilgisi, çalışma süresi ve yerel ağ IP'leri |
| `GET` | `/health` | Sağlık kontrolü |


Kimlik doğrulama başlığı: `X-Ogm-Api-Key`

---

## 8. Proje yapısı

```
Ogm.sln
src/
  Ogm.Shared/            Ortak sözleşmeler ve protokol sabitleri
    Protocol.cs          Yollar, port, başlık adları
    Contracts.cs         Kayıt/heartbeat/iş/durum kayıtları
    OgmJson.cs           Ortak JSON ayarları (camelCase, enum=string)
  Ogm.Agent/             İzlenen PC'lerde çalışan servis
    Print/               SpoolWatcher + .SHD meta veri ayrıştırma
    Queue/               Diskteki kalıcı outbox kuyruğu
    Transport/           Sunucu istemcisi (HTTP)
    Worker.cs            Arka plan döngüleri (heartbeat + yükleme)
    scripts/agent-service.cmd
  Ogm.Server/            Merkezi sunucu + panel
    Api/OgmApi.cs        Minimal API uç noktaları
    Storage/             ServerStore (kalıcı depo) + EventHub (SSE)
    wwwroot/             Panel: index.html, app.js, styles.css
scripts/
  server.cmd             Sunucu publish/run/debug/smoke
  smoke-test.ps1         Uçtan uca duman testi
```

---

## 9. Sorun giderme

| Belirti | Olası neden / çözüm |
|---|---|
| Panel açılıyor ama ajan görünmüyor | Ajanın `ServerUrl` değeri sunucunun LAN IP'si mi? Port 5099 güvenlik duvarında açık mı? |
| Ajan "401 Unauthorized" alıyor | Sunucu ve ajan `ApiKey` değerleri aynı değil |
| İşler hiç düşmüyor | Yazıcıda "yazdırılan belgeleri sakla" kapalı olabilir; ajan yönetici olarak çalışıyor mu? |
| Ajan kaydı geliyor, iş gelmiyor | Test yazdırması yapın; `%ProgramData%\Ogm\Agent\outbox` klasörüne bakın |
| `failed` klasörü büyüyor | İş boyutu `MaxPayloadBytes` sınırını aşıyor veya sunucu sürekli hata veriyor; sunucu loglarına bakın |

---

## 10. Sonraki adımlar (isteğe bağlı)

- Panel için kullanıcı adı/parola ile giriş (şu an panel yalnızca LAN'a açıktır)
- HTTPS/TLS desteği
- Yazıcı bazlı filtreleme ve raporlama
- Sunucu tarafında iş içeriğini sıkıştırma/arşivleme
