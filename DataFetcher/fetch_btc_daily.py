"""
fetch_btc_daily.py

Downloads BTCUSDT 1D (daily) kline data from the Binance public data repository
(https://data.binance.vision) and compiles it into a single CSV file.

    Datetime,Open,High,Low,Close,Volume

Data availability: 2017-08 onwards (we default to 2019-01 → 2025-12).

Output:
    data/btc_daily.csv

Usage:
    python3 fetch_btc_daily.py                      # 2019-01 → 2025-12
    python3 fetch_btc_daily.py --start 2021-01 --end 2023-12
    python3 fetch_btc_daily.py --out ./mydata/btc_daily.csv

Dependencies:
    pip install requests pandas
"""

import argparse
import io
import os
import zipfile

import pandas as pd
import requests

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------
BASE_URL   = "https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1d"
OUTPUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "data")
OUTPUT_FILE = os.path.join(OUTPUT_DIR, "btc_daily.csv")

# Binance kline column names (no header in the CSV)
KLINE_COLS = [
    "open_time", "Open", "High", "Low", "Close", "Volume",
    "close_time", "quote_volume", "trades",
    "taker_buy_base", "taker_buy_quote", "ignore",
]

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def iter_months(start_year: int, start_month: int,
                end_year: int,   end_month: int):
    """Yield (year, month) tuples inclusive of both endpoints."""
    y, m = start_year, start_month
    while (y, m) <= (end_year, end_month):
        yield y, m
        m += 1
        if m > 12:
            m = 1
            y += 1


def fetch_month(year: int, month: int):
    """Download one monthly ZIP and return a DataFrame, or None on failure."""
    filename = f"BTCUSDT-1d-{year}-{month:02d}.zip"
    url      = f"{BASE_URL}/{filename}"

    try:
        resp = requests.get(url, timeout=60)
        if resp.status_code == 404:
            return None
        resp.raise_for_status()
    except requests.RequestException as e:
        print(f"    ERROR: {e}")
        return None

    with zipfile.ZipFile(io.BytesIO(resp.content)) as z:
        csv_name = z.namelist()[0]
        with z.open(csv_name) as f:
            df = pd.read_csv(f, header=None, names=KLINE_COLS)

    return df


def normalise_timestamps(df: pd.DataFrame) -> pd.DataFrame:
    """Convert open_time to a proper Datetime column, handling ms vs us units."""
    sample = df["open_time"].iloc[0]
    unit = "us" if sample > 1e13 else "ms"

    df = df.copy()
    df["Datetime"] = pd.to_datetime(df["open_time"], unit=unit, utc=True).dt.floor("D")
    df = df.drop(columns=["open_time", "close_time", "quote_volume",
                           "trades", "taker_buy_base", "taker_buy_quote", "ignore"])
    df = df[["Datetime", "Open", "High", "Low", "Close", "Volume"]]
    return df


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def parse_args():
    p = argparse.ArgumentParser(
        description="Fetch BTCUSDT 1D data from Binance public data repo"
    )
    p.add_argument("--start", default="2019-01",
                   help="First month to fetch, YYYY-MM (default: 2019-01)")
    p.add_argument("--end",   default="2025-12",
                   help="Last month to fetch,  YYYY-MM (default: 2025-12)")
    p.add_argument("--out",   default=OUTPUT_FILE,
                   help="Output CSV file path (default: data/btc_daily.csv)")
    return p.parse_args()


if __name__ == "__main__":
    args = parse_args()

    start_year,  start_month  = map(int, args.start.split("-"))
    end_year,    end_month    = map(int, args.end.split("-"))
    output_file = args.out

    print(f"Fetching BTCUSDT 1D  {args.start} -> {args.end}")
    print(f"Source : {BASE_URL}")
    print(f"Output : {output_file}\n")

    all_frames = []
    months     = list(iter_months(start_year, start_month, end_year, end_month))

    for i, (year, month) in enumerate(months, 1):
        label = f"{year}-{month:02d}"
        print(f"  [{i:>3}/{len(months)}] {label} ...", end=" ", flush=True)

        df = fetch_month(year, month)
        if df is None:
            print("not found -- skipped")
            continue

        print(f"{len(df)} rows")
        df = normalise_timestamps(df)
        all_frames.append(df)

    if not all_frames:
        print("\nNo data downloaded. Check your date range or internet connection.")
        raise SystemExit(1)

    print("\nMerging and sorting ...")
    combined = pd.concat(all_frames, ignore_index=True)
    combined.sort_values("Datetime", inplace=True)
    combined.drop_duplicates(subset=["Datetime"], inplace=True)

    # Format datetime strings
    combined["Datetime"] = combined["Datetime"].dt.strftime("%Y-%m-%d %H:%M:%S+00:00")

    # Write to single CSV file
    os.makedirs(os.path.dirname(output_file), exist_ok=True)
    combined.to_csv(output_file, index=False)

    print(f"\nDone.")
    print(f"  Total candles : {len(combined):,}")
    print(f"  Output file   : {output_file}")
