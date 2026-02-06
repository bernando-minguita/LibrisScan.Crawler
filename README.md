# LibrisScan.Crawler

**LibrisScan** is a lightweight .NET 8 utility designed to automate the collection of book metadata and cover art. It recursively scans local directories for eBooks (PDF/ePub), cleans filenames to improve search accuracy, and fetches high-quality data via the Google Books API.

---

## 🚀 Key Features

* **Recursive File Scanning**: Automatically identifies all `.pdf` and `.epub` files within your source directories and subfolders.
* **ISBN Normalization**: Includes a built-in algorithm to convert legacy 10-digit ISBNs to the modern 13-digit format, ensuring consistent metadata.
* **Quota Management**: Proactively detects Google API rate limits (HTTP 429/403) and safely halts processing to protect your API standing.

---

## 🛠️ Configuration (`appsettings.json`)

The application uses an `appsettings.json` file to manage paths. You can point the crawler to any local or cloud-mapped drive.

```json
{
  "StorageSettings": {
    "JsonMetadataDir": "D:\\google_books\\isbn13",
    "CoversDir": "D:\\google_books\\covers",
    "EbookSourceDir": "D:\\E-Books Collections\\"
  }
}
