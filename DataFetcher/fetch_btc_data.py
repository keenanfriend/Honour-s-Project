"""
fetch_btc_data.py

Downloads BTCUSDT 1H kline data from the Binance public data repository
(https://data.binance.vision) and splits it into per-week CSV files that
match the btc_data.txt column format used by ChartApp:

    Datetime,Open,High,Low,Close,Volume

Data availability: 2017-08 onwards (we default to 2019-01 → 2025-12).

Output layout:
    data/YYYY-Www/btc_1h_YYYY-Www.csv
    e.g. data/2021-W01/btc_1h_2021-W01.csv

Usage:
    python3 fetch_btc_data.py                      # 2019-01 → 2025-12
    python3 fetch_btc_data.py --start 2021-01 --end 2023-12
    python3 fetch_btc_data.py --out ./mydata        # custom output folder

Dependencies:
    pip install requests pandas
"""

import argparse
import io
import os
import zipfile
from datetime import date

import pandas as pd
import requests

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------
BASE_URL   = "https://data.binance.vision/data/spot/monthly/klines/BTCUSDT/1h"
OUTPUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "data")  # shared data/ at repo root

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
    filename = f"BTCUSDT-1h-{year}-{month:02d}.zip"
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
    """Convert open_time to a proper Datetime column, handling ms vs µs units.
    Must be called per-month BEFORE concatenation to avoid mixed-unit overflows."""
    # Older files use milliseconds (~1.7e12 for 2025); newer files use microseconds (~1.7e15).
    sample = df["open_time"].iloc[0]
    unit = "us" if sample > 1e13 else "ms"

    df = df.copy()
    df["Datetime"] = pd.to_datetime(df["open_time"], unit=unit, utc=True).dt.floor("h")
    df = df.drop(columns=["open_time", "close_time", "quote_volume",
                           "trades", "taker_buy_base", "taker_buy_quote", "ignore"])
    df = df[["Datetime", "Open", "High", "Low", "Close", "Volume"]]
    return df

def save_by_week(df: pd.DataFrame, output_dir: str) -> int:
    """Split df by ISO week and write individual CSV files. Returns week count."""
    os.makedirs(output_dir, exist_ok=True)

    dt = pd.to_datetime(df["Datetime"], utc=True)
    iso = dt.dt.isocalendar()

    df = df.copy()
    df["_year"] = iso.year.astype(int)
    df["_week"] = iso.week.astype(int)

    groups = df.groupby(["_year", "_week"], sort=True)
    for (year, week), group in groups:
        week_label = f"{year}-W{week:02d}"
        folder     = os.path.join(output_dir, week_label)
        os.makedirs(folder, exist_ok=True)
        filepath   = os.path.join(folder, f"btc_1h_{week_label}.csv")
        group.drop(columns=["_year", "_week"]).to_csv(filepath, index=False)

    return len(groups)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def parse_args():
    p = argparse.ArgumentParser(
        description="Fetch BTCUSDT 1H data from Binance public data repo"
    )
    p.add_argument("--start", default="2019-01",
                   help="First month to fetch, YYYY-MM (default: 2019-01)")
    p.add_argument("--end",   default="2025-12",
                   help="Last month to fetch,  YYYY-MM (default: 2025-12)")
    p.add_argument("--out",   default=OUTPUT_DIR,
                   help="Output directory (default: ./data)")
    return p.parse_args()


if __name__ == "__main__":
    args = parse_args()

    start_year,  start_month  = map(int, args.start.split("-"))
    end_year,    end_month    = map(int, args.end.split("-"))
    output_dir = args.out

    print(f"Fetching BTCUSDT 1H  {args.start} → {args.end}")
    print(f"Source : {BASE_URL}")
    print(f"Output : {output_dir}\n")

    all_frames = []
    months     = list(iter_months(start_year, start_month, end_year, end_month))

    for i, (year, month) in enumerate(months, 1):
        label = f"{year}-{month:02d}"
        print(f"  [{i:>3}/{len(months)}] {label} ...", end=" ", flush=True)

        df = fetch_month(year, month)
        if df is None:
            print("not found — skipped")
            continue

        print(f"{len(df)} rows")
        # Normalise timestamps per-month to avoid mixing ms/µs units
        df = normalise_timestamps(df)
        all_frames.append(df)

    if not all_frames:
        print("\nNo data downloaded. Check your date range or internet connection.")
        raise SystemExit(1)

    print("\nMerging and sorting ...")
    combined = pd.concat(all_frames, ignore_index=True)
    combined.sort_values("Datetime", inplace=True)
    combined.drop_duplicates(subset=["Datetime"], inplace=True)

    # Format datetime strings to match btc_data.txt
    combined["Datetime"] = combined["Datetime"].dt.strftime("%Y-%m-%d %H:%M:%S+00:00")

    week_count = save_by_week(combined, output_dir)

    print(f"\nDone.")
    print(f"  Total candles : {len(combined):,}")
    print(f"  Weekly files  : {week_count}")
    print(f"  Location      : {output_dir}/")
