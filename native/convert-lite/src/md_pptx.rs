//! Markdown -> PPTX（Pandoc 3.x 对齐：每个标题开始新一页；无标题则全部段落一页）。
//!
//! 最小 PresentationML 包：slide -> slideLayout -> slideMaster -> theme 完整关系链，
//! 16:9 画布，标题大字 + 正文段落，粗体保留。诚实边界：不支持表格/图片/代码高亮。

use std::io::Write;
use std::path::Path;

use crate::doc::{Block, Document};
use crate::md_docx::parse_md_to_doc;

use zip::write::SimpleFileOptions;
use zip::ZipWriter;

const X: &str = "http://schemas.openxmlformats.org/drawingml/2006/main";
const R: &str = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const P: &str = "http://schemas.openxmlformats.org/presentationml/2006/main";
const CT: &str = "http://schemas.openxmlformats.org/package/2006/content-types";
const REL: &str = "http://schemas.openxmlformats.org/package/2006/relationships";

fn xml_esc(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

fn run_text(runs: &[crate::doc::Run]) -> String {
    runs.iter().map(|r| r.text.clone()).collect()
}

/// 一页：标题（可选）+ 正文段落。
struct Page {
    title: String,
    paras: Vec<String>,
}

/// 按标题分页：Heading(1-3) 起新页，其后段落归入该页，直到下一个标题。
fn to_pages(doc: &Document) -> Vec<Page> {
    let mut pages: Vec<Page> = Vec::new();
    let mut cur: Option<Page> = None;
    for b in &doc.blocks {
        match b {
            Block::Heading { runs, .. } => {
                if let Some(p) = cur.take() {
                    pages.push(p);
                }
                cur = Some(Page { title: run_text(runs), paras: Vec::new() });
            }
            Block::Paragraph { runs } => {
                let t = run_text(runs);
                if t.trim().is_empty() {
                    continue;
                }
                match cur.as_mut() {
                    Some(p) => p.paras.push(t),
                    None => {
                        cur = Some(Page { title: String::new(), paras: vec![t] });
                    }
                }
            }
            _ => {}
        }
    }
    if let Some(p) = cur.take() {
        pages.push(p);
    }
    if pages.is_empty() {
        pages.push(Page { title: String::new(), paras: Vec::new() });
    }
    pages
}

fn slide_xml(page: &Page, idx: usize) -> String {
    let mut body = String::new();
    // 标题文本框
    if !page.title.is_empty() {
        body.push_str(&format!(
            r#"<p:sp><p:nvSpPr><p:cNvPr id="2" name="Title {idx}"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
<p:spPr><a:xfrm><a:off x="457200" y="228600"/><a:ext cx="11277600" cy="1143000"/></a:xfrm>
<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
<p:txBody><a:bodyPr wrap="none"><a:spAutoFit/></a:bodyPr><a:lstStyle/>
<a:p><a:r><a:rPr lang="zh-CN" sz="4000" b="1"/><a:t>{}</a:t></a:r></a:p></p:txBody></p:sp>"#,
            xml_esc(&page.title)
        ));
    }
    // 正文文本框
    if !page.paras.is_empty() {
        let mut runs = String::new();
        for para in &page.paras {
            runs.push_str(&format!(
                r#"<a:p><a:r><a:rPr lang="zh-CN" sz="1800"/><a:t>{}</a:t></a:r></a:p>"#,
                xml_esc(para)
            ));
        }
        body.push_str(&format!(
            r#"<p:sp><p:nvSpPr><p:cNvPr id="3" name="Content {idx}"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
<p:spPr><a:xfrm><a:off x="457200" y="1600200"/><a:ext cx="11277600" cy="4876800"/></a:xfrm>
<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
<p:txBody><a:bodyPr wrap="square"/><a:lstStyle/>{runs}</p:txBody></p:sp>"#
        ));
    }
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sld xmlns:a="{X}" xmlns:r="{R}" xmlns:p="{P}">
<p:cSld><p:spTree>
<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
<p:grpSpPr/>{body}
</p:spTree></p:cSld>
<p:clrMapOvr><a:overrideClrMapping accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/></p:clrMapOvr>
</p:sld>"#
    )
}

fn slide_rels_xml() -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="{REL}">
<Relationship Id="rId1" Type="{R}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
</Relationships>"#
    )
}

fn layout_xml() -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sldLayout xmlns:a="{X}" xmlns:r="{R}" xmlns:p="{P}" type="blank" preserve="1">
<p:cSld name="Blank"><p:spTree>
<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
<p:grpSpPr/>
</p:spTree></p:cSld>
<p:clrMapOvr><a:overrideClrMapping accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/></p:clrMapOvr>
</p:sldLayout>"#
    )
}

fn layout_rels_xml() -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="{REL}">
<Relationship Id="rId1" Type="{R}/slideMaster" Target="../slideMasters/slideMaster1.xml"/>
</Relationships>"#
    )
}

fn master_xml() -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:sldMaster xmlns:a="{X}" xmlns:r="{R}" xmlns:p="{P}">
<p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>
<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
<p:grpSpPr/>
</p:spTree></p:cSld>
<p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
<p:sldLayoutIdLst><p:sldLayoutId id="1" r:id="rId1"/></p:sldLayoutIdLst>
<p:txStyles>
<p:titleStyle><a:lvl1pPr algn="l"><a:defRPr sz="4000" b="1"/></a:lvl1pPr></p:titleStyle>
<p:bodyStyle><a:lvl1pPr><a:defRPr sz="1800"/></a:lvl1pPr></p:bodyStyle>
<p:otherStyle/>
</p:txStyles>
</p:sldMaster>"#
    )
}

fn master_rels_xml() -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="{REL}">
<Relationship Id="rId1" Type="{R}/theme" Target="../theme/theme1.xml"/>
</Relationships>"#
    )
}

fn theme_xml() -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<a:theme xmlns:a="{X}" name="Office">
<a:themeElements>
<a:clrScheme name="Office">
<a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
<a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
<a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
<a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
<a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4>
<a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6>
<a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
</a:clrScheme>
<a:fontScheme name="Office">
<a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
<a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
</a:fontScheme>
<a:fmtScheme name="Office">
<a:fillStyleLst>
<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
<a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="50000"/><a:satMod val="300000"/></a:schemeClr></a:gs><a:gs pos="35000"><a:schemeClr val="phClr"><a:tint val="37000"/><a:satMod val="300000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:tint val="15000"/><a:satMod val="350000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="16200000" scaled="1"/></a:gradFill>
<a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:shade val="51000"/><a:satMod val="130000"/></a:schemeClr></a:gs><a:gs pos="80000"><a:schemeClr val="phClr"><a:shade val="93000"/><a:satMod val="130000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:shade val="94000"/><a:satMod val="135000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="16200000" scaled="0"/></a:gradFill>
</a:fillStyleLst>
<a:lnStyleLst>
<a:ln w="9525" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"><a:shade val="95000"/><a:satMod val="105000"/></a:schemeClr></a:solidFill><a:prstDash val="solid"/></a:ln>
<a:ln w="25400" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
<a:ln w="38100" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/></a:ln>
</a:lnStyleLst>
<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
<a:bgFillStyleLst>
<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
<a:solidFill><a:schemeClr val="phClr"><a:tint val="95000"/><a:satMod val="170000"/></a:schemeClr></a:solidFill>
<a:gradFill rotWithShape="1"><a:gsLst><a:gs pos="0"><a:schemeClr val="phClr"><a:tint val="93000"/><a:satMod val="150000"/><a:shade val="98000"/><a:lumMod val="102000"/></a:schemeClr></a:gs><a:gs pos="50000"><a:schemeClr val="phClr"><a:tint val="98000"/><a:satMod val="130000"/><a:shade val="90000"/><a:lumMod val="103000"/></a:schemeClr></a:gs><a:gs pos="100000"><a:schemeClr val="phClr"><a:shade val="63000"/><a:satMod val="120000"/></a:schemeClr></a:gs></a:gsLst><a:lin ang="16200000" scaled="0"/></a:gradFill>
</a:bgFillStyleLst>
</a:fmtScheme>
</a:themeElements>
</a:theme>"#
    )
}

fn content_types_xml(n: usize) -> String {
    let mut slides = String::new();
    for i in 1..=n {
        slides.push_str(&format!(
            r#"<Override PartName="/ppt/slides/slide{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>"#
        ));
    }
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="{CT}">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
<Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>
<Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>
<Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
{slides}</Types>"#
    )
}

fn root_rels_xml() -> String {
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="{REL}">
<Relationship Id="rId1" Type="{R}/officeDocument" Target="ppt/presentation.xml"/>
</Relationships>"#
    )
}

fn presentation_xml(n: usize) -> String {
    let mut ids = String::new();
    for i in 1..=n {
        ids.push_str(&format!(
            r#"<p:sldId id="{i}" r:id="rId{i}"/>"#
        ));
    }
    let p = format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<p:presentation xmlns:a="{X}" xmlns:r="{R}" xmlns:p="{P}">
<p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdMaster"/></p:sldMasterIdLst>
<p:sldIdLst>{ids}</p:sldIdLst>
<p:sldSz cx="12192000" cy="6858000"/>
<p:notesSz cx="6858000" cy="9144000"/>
</p:presentation>"#
    );
    p
}

fn presentation_rels_xml(n: usize) -> String {
    let mut rels = String::new();
    for i in 1..=n {
        rels.push_str(&format!(
            r#"<Relationship Id="rId{i}" Type="{R}/slide" Target="slides/slide{i}.xml"/>"#
        ));
    }
    format!(
        r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="{REL}">
<Relationship Id="rIdMaster" Type="{R}/slideMaster" Target="slideMasters/slideMaster1.xml"/>
{rels}</Relationships>"#
    )
}

/// Markdown 文件 -> PPTX（每标题一页）。
pub fn md_to_pptx(input: &Path, output: &Path) -> Result<(), String> {
    let md = std::fs::read_to_string(input).map_err(|e| e.to_string())?;
    let doc = parse_md_to_doc(&md);
    let pages = to_pages(&doc);
    let n = pages.len();

    let file = std::fs::File::create(output).map_err(|e| e.to_string())?;
    let mut zw = ZipWriter::new(file);
    let opts = SimpleFileOptions::default();
    let mut put = |name: &str, content: &str| -> Result<(), String> {
        zw.start_file(name, opts).map_err(|e| e.to_string())?;
        zw.write_all(content.as_bytes()).map_err(|e| e.to_string())
    };

    put("[Content_Types].xml", &content_types_xml(n))?;
    put("_rels/.rels", &root_rels_xml())?;
    put("ppt/presentation.xml", &presentation_xml(n))?;
    put("ppt/_rels/presentation.xml.rels", &presentation_rels_xml(n))?;
    put("ppt/slideMasters/slideMaster1.xml", &master_xml())?;
    put(
        "ppt/slideMasters/_rels/slideMaster1.xml.rels",
        &master_rels_xml(),
    )?;
    put("ppt/slideLayouts/slideLayout1.xml", &layout_xml())?;
    put(
        "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
        &layout_rels_xml(),
    )?;
    put("ppt/theme/theme1.xml", &theme_xml())?;
    for (i, page) in pages.iter().enumerate() {
        put(&format!("ppt/slides/slide{}.xml", i + 1), &slide_xml(page, i + 1))?;
        put(
            &format!("ppt/slides/_rels/slide{}.xml.rels", i + 1),
            &slide_rels_xml(),
        )?;
    }
    zw.finish().map_err(|e| e.to_string())?;
    Ok(())
}
