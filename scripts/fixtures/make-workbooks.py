# Writes the Excel workbooks scripts/verify-mount.ps1 loads. The .xlsx files are committed, so this
# only needs running again to change them: python scripts/fixtures/make-workbooks.py (needs openpyxl).
#
# Each sheet is one rule of how a sheet becomes a table - see ExcelWorkbook.cs - and the values that
# decide a column's type sit past row 500 on purpose, where a reader that samples would not look.
import datetime
import os

from openpyxl import Workbook
from openpyxl.chart import BarChart, Reference

here = os.path.dirname(os.path.abspath(__file__))


def budget():
    wb = Workbook()

    q1 = wb.active
    q1.title = "Q1"
    q1.append(["region", "amount", "units", "booked", "note", "active", "start"])
    for i in range(1, 601):
        booked = datetime.datetime(2024, 1, 1) + datetime.timedelta(days=i)
        if i == 590:
            booked += datetime.timedelta(hours=13, minutes=30)     # the only time of day
        q1.append([
            "north" if i % 2 else "south",
            12.5 if i == 560 else i * 10,                          # whole numbers, then a fraction
            i,                                                     # whole numbers throughout
            booked,
            "N/A" if i == 580 else i,                              # numbers, then text
            i % 3 == 0,
            datetime.time(8, 30) if i % 2 else datetime.time(17, 0),
        ])
        q1.cell(row=i + 1, column=7).number_format = "h:mm"
    q1.row_dimensions[10].hidden = True                            # filtered out in Excel, still data

    summary = wb.create_sheet("Summary Q1")
    summary.append(["region", "total"])
    summary.append(["north", 100])
    summary.append(["south", 200])

    # A report: a title above the table, which is further down and wider.
    report = wb.create_sheet("Report")
    report.append(["Quarterly report - do not edit"])
    report.append([])
    report.append(["region", "total"])
    report.append(["east", 5])

    wb.create_sheet("Empty")

    lookup = wb.create_sheet("Lookup")
    lookup.append(["code", "label"])
    lookup.append([1, "one"])
    lookup.sheet_state = "hidden"

    # The first row is numbers, so it is data and the columns are named by letter.
    no_header = wb.create_sheet("NoHeader")
    no_header.append([1, "alpha"])
    no_header.append([2, "beta"])

    # Starts in column B; one header blank, two alike, one wrapped; a blank row; an error value.
    messy = wb.create_sheet("Messy")
    messy["B1"] = "id"
    messy["C1"] = "amount"
    messy["E1"] = "amount"
    messy["F1"] = "Total\nCost"
    messy.append([None, 1, 5, "unlabelled", 7, 12])
    messy.append([])
    messy.append([None, 2, "#N/A", "also", 8, 13])

    # The same table name as Budget.Clash.csv, which is found first and keeps it.
    clash = wb.create_sheet("Clash")
    clash.append(["source"])
    clash.append(["from excel"])

    chart = wb.create_chartsheet("Chart")
    bars = BarChart()
    bars.add_data(Reference(summary, min_col=2, min_row=1, max_row=3), titles_from_data=True)
    chart.add_chart(bars)

    wb.save(os.path.join(here, "Budget.xlsx"))


def plan():
    wb = Workbook()
    targets = wb.active
    targets.title = "Targets"
    targets.append(["region", "target"])
    targets.append(["north", 1000])
    wb.save(os.path.join(here, "Plan.2024.xlsx"))


def macro():
    wb = Workbook()
    data = wb.active
    data.title = "Data"
    data.append(["key", "value"])
    data.append(["k1", 42])
    wb.save(os.path.join(here, "Macro.xlsm"))


budget()
plan()
macro()
print("wrote Budget.xlsx, Plan.2024.xlsx, Macro.xlsm to", here)
