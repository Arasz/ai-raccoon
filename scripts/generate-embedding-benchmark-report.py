#!/usr/bin/env python3
"""
Generates an HTML report from embedding benchmark data, with matplotlib charts.
Reads benchmark output from CLI args or stdin, and produces a self-contained HTML file.
"""

import argparse
import base64
import io
import json
import os
import re
import sys
import textwrap
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
import numpy as np

# ── styling ──────────────────────────────────────────────────────────────────
plt.rcParams.update({
    "font.family": "sans-serif",
    "font.sans-serif": ["Helvetica Neue", "Helvetica", "Arial"],
    "axes.facecolor": "#f8f9fa",
    "figure.facecolor": "white",
    "axes.edgecolor": "#dee2e6",
    "axes.grid": True,
    "grid.alpha": 0.3,
    "grid.color": "#adb5bd",
    "axes.spines.top": False,
    "axes.spines.right": False,
    "xtick.labelsize": 11,
    "ytick.labelsize": 11,
    "axes.labelsize": 12,
    "axes.titlesize": 14,
})


# ── data model ───────────────────────────────────────────────────────────────
EmbedderData = dict  # {"name": str, "dim": int, "r5": float, "r10": float, "mrr": float, "ndcg": float, "latency_ms": float, "allocated_kb": float, "model_size_mb": float}


def parse_benchmark_output(text: str) -> list[EmbedderData]:
    """Parse the quality comparison table from benchmark output."""
    models = []
    
    lines = text.strip().split("\n")
    for line in lines:
        # Match lines like: local:all-MiniLM-L6-v2.Q5_K_M.gguf   384   0.325  0.378  0.836  0.607
        m = re.match(
            r"^\s*(local|lmstudio|onnx):(.+?)\s+(\d+)\s+([\d.,]+)\s+([\d.,]+)\s+([\d.,]+)\s+([\d.,]+)",
            line
        )
        if m:
            prefix = m.group(1) + ":"
            name_part = m.group(2).strip()
            dim = int(m.group(3))
            r5 = float(m.group(4).replace(",", "."))
            r10 = float(m.group(5).replace(",", "."))
            mrr = float(m.group(6).replace(",", "."))
            ndcg = float(m.group(7).replace(",", "."))
            
            display_name = f"{prefix}{name_part}"
            short_name = name_part.split("/")[-1] if "/" in name_part else name_part[:40]
            
            models.append({
                "name": display_name,
                "short_name": short_name,
                "dim": dim,
                "r5": r5,
                "r10": r10,
                "mrr": mrr,
                "ndcg": ndcg,
                "type": "Local (ONNX)" if prefix == "onnx:" else "Local (GGUF)" if prefix == "local:" else "Remote (LM Studio)",
                "latency_ms": None,
                "allocated_kb": None,
                "model_size_mb": None,
            })
    
    return models


def parse_latency_output(text: str) -> list[dict]:
    """Parse BenchmarkDotNet latency results."""
    results = []
    
    in_table = False
    for line in text.strip().split("\n"):
        # Match BenchmarkDotNet summary lines
        if "| Search |" in line and "|" in line:
            parts = [p.strip() for p in line.split("|")]
            # Filter out empty parts
            parts = [p for p in parts if p]
            if len(parts) >= 4:
                # Method | Embedder | Mean | Allocated (or similar)
                name = parts[0] if len(parts) > 0 else ""
                embedder = parts[1] if len(parts) > 1 else ""
                mean = parts[2] if len(parts) > 2 else ""
                allocated = parts[3] if len(parts) > 3 else ""
                
                # Parse mean time
                mean_ms = None
                if mean:
                    m2 = re.match(r"([\d.]+)\s*ms", mean)
                    if m2:
                        mean_ms = float(m2.group(1))
                    m3 = re.match(r"([\d.]+)\s*us", mean)
                    if m3:
                        mean_ms = float(m3.group(1)) / 1000
                
                # Parse allocated
                alloc_kb = None
                if allocated:
                    a1 = re.match(r"([\d.]+)\s*KB", allocated)
                    if a1:
                        alloc_kb = float(a1.group(1))
                
                results.append({
                    "name": embedder,
                    "latency_ms": mean_ms,
                    "allocated_kb": alloc_kb,
                })
    
    return results


def merge_data(quality: list[EmbedderData], latency: list[dict]) -> list[EmbedderData]:
    """Merge latency data into quality data."""
    by_name = {}
    for m in quality:
        by_name[m["name"]] = m
    
    for l in latency:
        name = l["name"]
        if name in by_name:
            by_name[name]["latency_ms"] = l["latency_ms"]
            by_name[name]["allocated_kb"] = l["allocated_kb"]
    
    return list(by_name.values())


# ── chart generators ─────────────────────────────────────────────────────────

def render_quality_comparison(models: list[EmbedderData]) -> str:
    """Bar chart comparing MRR and nDCG across models."""
    names = [m["short_name"][:25] for m in models]
    x = np.arange(len(names))
    width = 0.35
    
    fig, ax = plt.subplots(figsize=(10, 5.5))
    
    bars1 = ax.bar(x - width/2, [m["mrr"] for m in models], width, 
                   label="MRR", color="#4c72b0", edgecolor="white", linewidth=0.5)
    bars2 = ax.bar(x + width/2, [m["ndcg"] for m in models], width,
                   label="nDCG@10", color="#dd8452", edgecolor="white", linewidth=0.5)
    
    ax.set_ylabel("Score (higher is better)")
    ax.set_title("Retrieval Quality Comparison", fontweight="bold")
    ax.set_xticks(x)
    ax.set_xticklabels(names, rotation=25, ha="right", fontsize=9)
    ax.legend(loc="lower right")
    ax.set_ylim(0, 1.05)
    ax.yaxis.set_major_formatter(mticker.FormatStrFormatter("%.2f"))
    
    for bar in bars1:
        h = bar.get_height()
        ax.annotate(f"{h:.3f}", xy=(bar.get_x() + bar.get_width()/2, h),
                    xytext=(0, 3), textcoords="offset points", ha="center", fontsize=8)
    for bar in bars2:
        h = bar.get_height()
        ax.annotate(f"{h:.3f}", xy=(bar.get_x() + bar.get_width()/2, h),
                    xytext=(0, 3), textcoords="offset points", ha="center", fontsize=8)
    
    plt.tight_layout()
    return _fig_to_base64(fig)


def render_recall_comparison(models: list[EmbedderData]) -> str:
    """Grouped bar chart for R@5 and R@10."""
    names = [m["short_name"][:25] for m in models]
    x = np.arange(len(names))
    width = 0.35
    
    fig, ax = plt.subplots(figsize=(10, 5.5))
    
    bars1 = ax.bar(x - width/2, [m["r5"] for m in models], width,
                   label="Recall@5", color="#55a868", edgecolor="white", linewidth=0.5)
    bars2 = ax.bar(x + width/2, [m["r10"] for m in models], width,
                   label="Recall@10", color="#c44e52", edgecolor="white", linewidth=0.5)
    
    ax.set_ylabel("Recall (higher is better)")
    ax.set_title("Recall Comparison", fontweight="bold")
    ax.set_xticks(x)
    ax.set_xticklabels(names, rotation=25, ha="right", fontsize=9)
    ax.legend(loc="lower right")
    ax.set_ylim(0, 1.05)
    ax.yaxis.set_major_formatter(mticker.FormatStrFormatter("%.2f"))
    
    for bar in bars1:
        h = bar.get_height()
        ax.annotate(f"{h:.3f}", xy=(bar.get_x() + bar.get_width()/2, h),
                    xytext=(0, 3), textcoords="offset points", ha="center", fontsize=8)
    for bar in bars2:
        h = bar.get_height()
        ax.annotate(f"{h:.3f}", xy=(bar.get_x() + bar.get_width()/2, h),
                    xytext=(0, 3), textcoords="offset points", ha="center", fontsize=8)
    
    plt.tight_layout()
    return _fig_to_base64(fig)


def render_latency_chart(models: list[EmbedderData]) -> str:
    """Horizontal bar chart for latency."""
    names = [m["short_name"][:25] for m in models]
    latencies = [m.get("latency_ms") for m in models]
    
    fig, ax = plt.subplots(figsize=(10, 4.5))
    
    valid = [(n, l) for n, l in zip(names, latencies) if l is not None]
    if not valid:
        plt.close(fig)
        return ""
    
    vnames, vlat = zip(*valid)
    colors = ["#4c72b0" if "GGUF" in m["type"] else "#dd8452" if "ONNX" in m["type"] else "#55a868" 
              for m in models if m.get("latency_ms") is not None]
    
    bars = ax.barh(vnames, vlat, color=colors, edgecolor="white", linewidth=0.5, height=0.6)
    ax.set_xlabel("Latency (ms) — lower is better")
    ax.set_title("Search Latency per Query", fontweight="bold")
    ax.xaxis.set_major_formatter(mticker.FormatStrFormatter("%.0f ms"))
    
    for bar, v in zip(bars, vlat):
        ax.annotate(f"{v:.1f} ms", xy=(bar.get_width(), bar.get_y() + bar.get_height()/2),
                    xytext=(5, 0), textcoords="offset points", ha="left", va="center", fontsize=9)
    
    plt.tight_layout()
    return _fig_to_base64(fig)


def render_model_size_chart(models: list[EmbedderData]) -> str:
    """Bar chart for model sizes."""
    names = [m["short_name"][:25] for m in models]
    sizes = [m.get("model_size_mb", 0) for m in models]
    
    if not any(s for s in sizes):
        return ""
    
    fig, ax = plt.subplots(figsize=(10, 4.5))
    colors = ["#4c72b0" if "GGUF" in m["type"] else "#dd8452" if "ONNX" in m["type"] else "#55a868" 
              for m in models]
    
    bars = ax.bar(names, sizes, color=colors, edgecolor="white", linewidth=0.5)
    ax.set_ylabel("Model Size (MB)")
    ax.set_title("Model Disk Footprint", fontweight="bold")
    ax.set_xticklabels(names, rotation=25, ha="right", fontsize=9)
    
    for bar, s in zip(bars, sizes):
        if s > 0:
            h = bar.get_height()
            label = f"{s:.0f} MB" if s < 1000 else f"{s/1000:.1f} GB"
            ax.annotate(label, xy=(bar.get_x() + bar.get_width()/2, h),
                        xytext=(0, 3), textcoords="offset points", ha="center", fontsize=9)
    
    plt.tight_layout()
    return _fig_to_base64(fig)


def render_dimension_chart(models: list[EmbedderData]) -> str:
    """Chart showing embedding dimensions."""
    names = [m["short_name"][:25] for m in models]
    dims = [m["dim"] for m in models]
    
    fig, ax = plt.subplots(figsize=(10, 4.5))
    colors = ["#4c72b0" if "GGUF" in m["type"] else "#dd8452" if "ONNX" in m["type"] else "#55a868" 
              for m in models]
    
    bars = ax.bar(names, dims, color=colors, edgecolor="white", linewidth=0.5)
    ax.set_ylabel("Dimensions")
    ax.set_title("Embedding Vector Dimensions", fontweight="bold")
    ax.set_xticklabels(names, rotation=25, ha="right", fontsize=9)
    
    for bar, d in zip(bars, dims):
        h = bar.get_height()
        ax.annotate(str(d), xy=(bar.get_x() + bar.get_width()/2, h),
                    xytext=(0, 3), textcoords="offset points", ha="center", fontsize=10, fontweight="bold")
    
    plt.tight_layout()
    return _fig_to_base64(fig)


def render_radar_chart(models: list[EmbedderData]) -> str:
    """Radar chart comparing the top 3 models across all quality metrics."""
    if len(models) < 2:
        return ""
    
    top = sorted(models, key=lambda m: m["ndcg"], reverse=True)[:4]
    metrics = ["R@5", "R@10", "MRR", "nDCG@10"]
    angles = np.linspace(0, 2 * np.pi, len(metrics), endpoint=False).tolist()
    angles += angles[:1]
    
    fig, ax = plt.subplots(figsize=(7, 7), subplot_kw={"projection": "polar"})
    ax.set_theta_offset(np.pi / 2)
    ax.set_theta_direction(-1)
    ax.set_xticks(angles[:-1])
    ax.set_xticklabels(metrics, fontsize=11)
    ax.set_ylim(0, 1)
    ax.set_title("Quality Profile (top models)", fontweight="bold", pad=20)
    
    colors = ["#4c72b0", "#dd8452", "#55a868", "#c44e52"]
    for i, m in enumerate(top):
        values = [m["r5"], m["r10"], m["mrr"], m["ndcg"]]
        values += values[:1]
        ax.plot(angles, values, "o-", linewidth=2, label=m["short_name"][:20], color=colors[i % 4])
        ax.fill(angles, values, alpha=0.1, color=colors[i % 4])
    
    ax.legend(loc="upper right", bbox_to_anchor=(1.3, 1.1), fontsize=9)
    plt.tight_layout()
    return _fig_to_base64(fig)


def _fig_to_base64(fig) -> str:
    buf = io.BytesIO()
    fig.savefig(buf, format="png", dpi=150, bbox_inches="tight")
    plt.close(fig)
    buf.seek(0)
    return base64.b64encode(buf.read()).decode()


# ── HTML report generator ────────────────────────────────────────────────────

def generate_html(models: list[EmbedderData], benchmark_text: str, latency_text: str = "", title: str = "AiRaccoon Embedding Model Benchmark Report") -> str:
    """Generate a complete self-contained HTML report."""
    
    # Sort by nDCG descending for display
    sorted_models = sorted(models, key=lambda m: m["ndcg"], reverse=True)
    
    # Generate charts
    quality_chart = render_quality_comparison(sorted_models)
    recall_chart = render_recall_comparison(sorted_models)
    latency_chart = render_latency_chart(sorted_models)
    dimension_chart = render_dimension_chart(sorted_models)
    radar_chart = render_radar_chart(sorted_models)
    
    # Build the data table rows
    table_rows = ""
    
    # Compute bests outside the loop
    best_r5 = max(mm["r5"] for mm in sorted_models)
    best_mrr = max(mm["mrr"] for mm in sorted_models)
    best_ndcg = max(mm["ndcg"] for mm in sorted_models)
    latencies = [mm.get("latency_ms") for mm in sorted_models if mm.get("latency_ms") is not None]
    best_latency = min(latencies) if latencies else 0

    def make_tag(t):
        return t.lower().replace(" ", "-").replace("(", "").replace(")", "")

    for i, m in enumerate(sorted_models):
        
        def badge(val, best_val, high_good=True):
            if best_val == 0: return ""
            ratio = val / best_val
            if (high_good and ratio >= 0.95) or (not high_good and ratio <= 1.05):
                return "🥇"
            elif (high_good and ratio >= 0.85) or (not high_good and ratio <= 1.5):
                return "👍"
            return ""
        
        lat_str = f"{m.get('latency_ms', 0):.1f}" if m.get("latency_ms") else "—"
        alloc_str = f"{m.get('allocated_kb', 0):.0f}" if m.get("allocated_kb") else "—"
        size_str = f"{m.get('model_size_mb', 0):.0f}" if m.get("model_size_mb") else "—"
        
        table_rows += f"""
        <tr>
            <td><span class="model-tag tag-{make_tag(m['type'])}">{m['type']}</span></td>
            <td title="{m['name']}">{m['short_name'][:35]}</td>
            <td>{m['dim']}</td>
            <td class="num">{m['r5']:.3f}</td>
            <td class="num">{m['r10']:.3f}</td>
            <td class="num">{m['mrr']:.3f} {badge(m['mrr'], best_mrr)}</td>
            <td class="num">{m['ndcg']:.3f} {badge(m['ndcg'], best_ndcg)}</td>
            <td class="num">{lat_str} {badge(m.get('latency_ms', 0) or 999, best_latency, False)}</td>
            <td class="num">{alloc_str}</td>
        </tr>"""
    
    # Highlight the best model
    best = sorted_models[0] if sorted_models else None
    
    # Build comparison text
    comparison_html = ""
    if len(sorted_models) >= 2:
        baseline = sorted_models[-1]  # Assume last is baseline (all-MiniLM)
        top = sorted_models[0]
        
        comparison_html = f"""
        <div class="comparison-box">
            <h3>📊 Key Findings</h3>
            <ul>
                <li><strong>Best overall quality:</strong> <code>{top['short_name'][:40]}</code> 
                    — nDCG@10 {top['ndcg']:.3f}, MRR {top['mrr']:.3f}</li>
                <li><strong>vs baseline</strong> <code>{baseline['short_name'][:40]}</code>:
                    nDCG Δ={top['ndcg'] - baseline['ndcg']:+.3f}, MRR Δ={top['mrr'] - baseline['mrr']:+.3f}</li>"""
        
        # Latency comparison
        models_with_latency = [m for m in sorted_models if m.get("latency_ms")]
        if models_with_latency:
            fastest = min(models_with_latency, key=lambda m: m["latency_ms"])
            slowest = max(models_with_latency, key=lambda m: m["latency_ms"])
            comparison_html += f"""
                <li><strong>Fastest:</strong> <code>{fastest['short_name'][:30]}</code> at {fastest['latency_ms']:.1f} ms/query</li>
                <li><strong>Slowest:</strong> <code>{slowest['short_name'][:30]}</code> at {slowest['latency_ms']:.1f} ms/query
                    (×{slowest['latency_ms']/fastest['latency_ms']:.1f} slower)</li>"""
        
        comparison_html += """
            </ul>
        </div>"""
    
    # Charts HTML
    charts_html = f"""
    <div class="chart-grid">
        <div class="chart-card"><img src="data:image/png;base64,{quality_chart}" alt="Quality Comparison"/></div>
        <div class="chart-card"><img src="data:image/png;base64,{recall_chart}" alt="Recall Comparison"/></div>
        <div class="chart-card"><img src="data:image/png;base64,{dimension_chart}" alt="Dimension Comparison"/></div>"""
    
    if latency_chart:
        charts_html += f'\n        <div class="chart-card"><img src="data:image/png;base64,{latency_chart}" alt="Latency"/></div>'
    if radar_chart:
        charts_html += f'\n        <div class="chart-card full-width"><img src="data:image/png;base64,{radar_chart}" alt="Radar Profile"/></div>'
    
    charts_html += "\n    </div>"
    
    # Raw benchmark output (collapsible)
    escaped_bench = benchmark_text[:5000].replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    
    html = f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>{title}</title>
<style>
    * {{ margin: 0; padding: 0; box-sizing: border-box; }}
    body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
           background: #f5f7fa; color: #1a1a2e; line-height: 1.6; }}
    .container {{ max-width: 1200px; margin: 0 auto; padding: 24px 20px; }}
    
    header {{ text-align: center; padding: 32px 0 24px; }}
    header h1 {{ font-size: 28px; font-weight: 700; color: #16213e; }}
    header p {{ color: #6c757d; font-size: 14px; margin-top: 4px; }}
    header .badge {{ display: inline-block; background: #4c72b0; color: white;
                     padding: 2px 10px; border-radius: 12px; font-size: 12px; margin: 0 3px; }}
    
    .comparison-box {{ background: white; border-radius: 12px; padding: 24px;
                       box-shadow: 0 1px 3px rgba(0,0,0,0.08); margin: 24px 0; }}
    .comparison-box h3 {{ font-size: 18px; margin-bottom: 12px; color: #16213e; }}
    .comparison-box ul {{ padding-left: 20px; }}
    .comparison-box li {{ margin: 6px 0; font-size: 14px; }}
    .comparison-box code {{ background: #e9ecef; padding: 2px 6px; border-radius: 4px; font-size: 13px; }}
    
    table {{ width: 100%; border-collapse: collapse; background: white; border-radius: 12px;
             overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,0.08); margin: 24px 0; font-size: 13px; }}
    th {{ background: #16213e; color: white; padding: 12px 10px; text-align: left;
          font-weight: 600; white-space: nowrap; }}
    td {{ padding: 10px; border-bottom: 1px solid #f0f0f0; }}
    tr:last-child td {{ border-bottom: none; }}
    tr:hover {{ background: #f8f9fa; }}
    .num {{ text-align: right; font-variant-numeric: tabular-nums; font-family: 'SF Mono', 'Menlo', 'Consolas', monospace; }}
    .model-tag {{ display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 11px; font-weight: 500; white-space: nowrap; }}
    .tag-local-gguf {{ background: #d4edda; color: #155724; }}
    .tag-local-onnx {{ background: #ffeeba; color: #856404; }}
    .tag-remote-lm-studio {{ background: #cce5ff; color: #004085; }}
    
    .chart-grid {{ display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin: 24px 0; }}
    .chart-card {{ background: white; border-radius: 12px; padding: 16px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }}
    .chart-card img {{ width: 100%; height: auto; display: block; }}
    .full-width {{ grid-column: 1 / -1; }}
    
    .raw-output {{ background: #1e1e2e; color: #cdd6f4; border-radius: 12px; padding: 16px;
                   font-family: 'SF Mono', 'Menlo', 'Consolas', monospace; font-size: 12px;
                   overflow-x: auto; margin: 24px 0; line-height: 1.4; white-space: pre-wrap; }}
    .raw-toggle {{ cursor: pointer; color: #4c72b0; text-decoration: underline; font-size: 13px; }}
    
    footer {{ text-align: center; padding: 24px; color: #6c757d; font-size: 12px; }}
    
    @media (max-width: 768px) {{
        .chart-grid {{ grid-template-columns: 1fr; }}
        table {{ font-size: 11px; }}
        th, td {{ padding: 8px 6px; }}
    }}
</style>
</head>
<body>
<div class="container">

<header>
    <h1>🔬 AiRaccoon Embedding Benchmark Report</h1>
    <p>Generated {__import__('datetime').datetime.now().strftime('%Y-%m-%d %H:%M')} 
       <span class="badge">{len(models)} models</span>
       <span class="badge">{sorted_models[0]['dim'] if sorted_models else 0} dim max</span>
    </p>
</header>

{comparison_html}

<table>
    <thead>
        <tr>
            <th>Type</th>
            <th>Model</th>
            <th>Dim</th>
            <th class="num">R@5</th>
            <th class="num">R@10</th>
            <th class="num">MRR</th>
            <th class="num">nDCG@10</th>
            <th class="num">Latency</th>
            <th class="num">Alloc</th>
        </tr>
    </thead>
    <tbody>
        {table_rows}
    </tbody>
</table>

{charts_html}

<details>
    <summary class="raw-toggle">View raw benchmark output</summary>
    <pre class="raw-output">{escaped_bench}</pre>
</details>

<footer>
    <p>AiRaccoon Benchmark Suite — <code>benchmarks/AiRaccoon.Benchmarks/</code></p>
</footer>

</div>
</body>
</html>"""
    
    return html


# ── main ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Generate embedding benchmark HTML report")
    parser.add_argument("--quality", type=str, help="Path to file with quality benchmark output")
    parser.add_argument("--latency", type=str, help="Path to file with latency benchmark output")
    parser.add_argument("--output", "-o", type=str, default="embedding-benchmark-report.html",
                        help="Output HTML file path")
    parser.add_argument("--title", type=str, default="AiRaccoon Embedding Model Benchmark Report",
                        help="Report title")
    parser.add_argument("--stdin", action="store_true", help="Read benchmark output from stdin")
    args = parser.parse_args()
    
    # Read benchmark output
    quality_text = ""
    latency_text = ""

    if args.stdin:
        quality_text = sys.stdin.read()
    elif args.quality:
        with open(args.quality) as f:
            quality_text = f.read()
    if args.latency:
        with open(args.latency) as f:
            latency_text = f.read()
    
    if not quality_text:
        print("No benchmark data provided. Use --quality <file> or --stdin.")
        sys.exit(1)
    
    # Parse
    models = parse_benchmark_output(quality_text)
    if not models:
        print(f"Warning: No model data parsed from input. Check the format.")
        print(f"Input length: {len(quality_text)} chars")
        print(f"First 500 chars:\n{quality_text[:500]}")
        # Still generate a report with whatever we can
        return 1
    
    # Try to parse latency
    if latency_text:
        latency_data = parse_latency_output(latency_text)
        models = merge_data(models, latency_data)
    
    # Estimate model sizes (from manifests)
    model_dirs = {
        "all-MiniLM-L6-v2": 21,
        "all-MiniLM-L6-v2.Q5_K_M": 21,
        "qwen3-embedding-0.6b": 639,
        "embeddinggemma-300m": 334,
        "SFR-Embedding-Code-400M_R": 1746,
        "code-daemon-embed-v1": 187,
        "model_qint8_arm64": 23,
    }
    for m in models:
        for key, size in model_dirs.items():
            if key in m["name"]:
                m["model_size_mb"] = size
                break
    
    html = generate_html(models, quality_text, latency_text, args.title)
    
    with open(args.output, "w") as f:
        f.write(html)
    
    print(f"Report written to {args.output}")
    print(f"  Models: {len(models)}")
    print(f"  File size: {len(html):,} bytes")
    
    # Print summary
    print("\nModel Summary:")
    print(f"  {'Model':<50} {'Dim':<6} {'R@5':<8} {'R@10':<8} {'MRR':<8} {'nDCG@10':<8}")
    print("  " + "-" * 88)
    for m in sorted(models, key=lambda x: x["ndcg"], reverse=True):
        print(f"  {m['short_name'][:48]:<50} {m['dim']:<6} {m['r5']:<8.3f} {m['r10']:<8.3f} {m['mrr']:<8.3f} {m['ndcg']:<8.3f}")
    
    return 0


if __name__ == "__main__":
    sys.exit(main())