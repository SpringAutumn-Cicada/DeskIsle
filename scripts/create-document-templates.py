"""Regenerate embedded source templates; Python is unnecessary for build/runtime.
Use PYTHONPATH=.tools/template-libs (python-pptx 1.0.2, openpyxl 3.1.5),
plus python-docx and reportlab. Never writes user documents.
"""
from pathlib import Path
from datetime import datetime
from docx import Document
from openpyxl import Workbook
from pptx import Presentation
from reportlab.pdfgen.canvas import Canvas
from reportlab.lib.pagesizes import A4

root = Path(__file__).resolve().parents[1] / 'src/DeskIsle/Templates'
root.mkdir(parents=True, exist_ok=True)

def metadata(p):
    p.title = ''
    p.subject = ''
    p.author = 'DeskIsle'
    p.last_modified_by = 'DeskIsle'
    p.comments = ''
    p.created = datetime(2000, 1, 1)
    p.modified = datetime(2000, 1, 1)

doc = Document()
metadata(doc.core_properties)
doc.add_paragraph()
doc.save(root / 'Blank.docx')

book = Workbook()
book.active.title = 'Sheet1'
book.properties.creator = 'DeskIsle'
book.properties.lastModifiedBy = 'DeskIsle'
book.properties.created = datetime(2000, 1, 1)
book.properties.modified = datetime(2000, 1, 1)
book.save(root / 'Blank.xlsx')

slides = Presentation()
metadata(slides.core_properties)
slides.slides.add_slide(slides.slide_layouts[6])
slides.save(root / 'Blank.pptx')

pdf = Canvas(str(root / 'Blank.pdf'), pagesize=A4, invariant=1)
pdf.setAuthor('DeskIsle')
pdf.setTitle('')
pdf.showPage()
pdf.save()
