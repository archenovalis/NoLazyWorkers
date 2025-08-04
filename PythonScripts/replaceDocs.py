import argparse
import sys
import logging
import re
import os
from typing import Dict, List, Tuple
from tree_sitter import Language, Parser, Node
import tree_sitter_c_sharp

# Set up logging
logging.basicConfig(level=logging.DEBUG, handlers=[logging.StreamHandler()])
logger = logging.getLogger(__name__)

# Initialize tree-sitter parser for C#
PARSER = Parser()
PARSER.language=(Language(tree_sitter_c_sharp.language()))

def normalize_signature(signature: str, node_type: str) -> str:
    """Normalize a signature for matching while preserving type constraints."""
    logger.debug(f"Raw signature before normalization (type: {node_type}): {signature}")
    # Split signature to handle constraints separately
    parts = signature.split('where')
    main_signature = parts[0].strip()
    constraints = 'where' + 'where'.join(parts[1:]) if len(parts) > 1 else ''

    # Remove extra whitespace and newlines from main signature
    main_signature = re.sub(r'\s+', ' ', main_signature).strip()

    # For methods and constructors, join parameter types and names (e.g., int index -> intindex)
    if node_type in ('method_declaration', 'constructor_declaration'):
        def normalize_params(match):
            param = match.group(0)
            param = re.sub(r'\s+', '', param)
            return param
        main_signature = re.sub(r'(\w+\s+\w+(?:<[^>]+>)?\s+\w+)', normalize_params, main_signature)

    # Normalize main signature by removing spaces between tokens
    normalized_main = ''.join(main_signature.split())

    # Normalize constraints by removing spaces between tokens, but preserve 'where' structure
    if constraints:
        constraints = re.sub(r'\s+', '', constraints)
        normalized = normalized_main + constraints
    else:
        normalized = normalized_main

    # Remove trailing `{` or `}` or both
    normalized = re.sub(r'[{}]+$', '', normalized)

    return normalized

def parse_doc_file(content: str) -> Dict[str, Tuple[str, str, str]]:
    """Parse the documentation file to extract XML documentation, signatures, and attributes."""
    doc_dict = {}
    lines = content.splitlines()
    i = 0
    while i < len(lines):
        line = lines[i].strip()
        if line.startswith('///'):
            doc_lines = []
            while i < len(lines) and lines[i].strip().startswith('///'):
                doc_lines.append(lines[i].strip())
                i += 1
            attr_lines = []
            sig_lines = []
            while i < len(lines) and not lines[i].strip().startswith('///'):
                if lines[i].strip():
                    if lines[i].strip().startswith('['):
                        attr_lines.append(lines[i].strip())
                    else:
                        sig_lines.append(lines[i].strip())
                i += 1
            attributes = '\n'.join(attr_lines)
            signature = ' '.join(sig_lines)
            if signature:
                node_type = (
                    'method_declaration' if '(' in signature and ')' in signature and not signature.split()[0] in ('struct', 'class', 'interface', 'enum') else
                    'constructor_declaration' if '(' in signature and ')' in signature and signature.split()[0] in ('struct', 'class') else
                    'enum_declaration' if signature.startswith('enum') else
                    'struct_declaration' if signature.startswith('struct') else
                    'interface_declaration' if signature.startswith('interface') else
                    'class_declaration'
                )
                normalized_sig = normalize_signature(signature, node_type)
                if normalized_sig:
                    doc_dict[normalized_sig] = (node_type, '\n'.join(doc_lines), attributes)
                    logger.debug(f"Stored doc signature: {normalized_sig} (type: {node_type})")
            else:
                logger.debug(f"Skipped empty signature at line {i}")
        else:
            i += 1
    logger.debug(f"Doc dictionary keys: {list(doc_dict.keys())}")
    return doc_dict

def extract_method_info(node: Node, source_text: str) -> Tuple[str, str, str, int, int, str, str]:
    """Extract method signature, documentation, attributes, and byte offsets from a method node."""
    signature = ''
    attributes = ''
    signature_content = ''
    signature_end = node.start_byte
    attr_start = node.start_byte
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
            signature_content += attr_text + '\n'
            attr_start = min(attr_start, child.start_byte)
        elif child.type in ('modifier', 'void_type', 'predefined_type', 'identifier', 'generic_name', 'type_parameter_list'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
        elif child.type == 'parameter_list':
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
            signature_end = child.end_byte
        elif child.type == 'type_parameter_constraints_clause':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
            signature_end = child.end_byte
    signature = signature.strip()
    signature_content = signature_content.strip()
    normalized_sig = normalize_signature(signature, 'method_declaration')
    doc = ''
    doc_start = attr_start
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
            doc_start = min(doc_start, prev_sibling.start_byte)
        prev_sibling = prev_sibling.prev_sibling
    start_byte = doc_start
    logger.debug(f"Detected source signature: {normalized_sig} (type: method_declaration)")
    return (normalized_sig, doc.strip(), attributes.strip(), start_byte, signature_end, 'method_declaration', signature_content)

def extract_constructor_info(node: Node, source_text: str) -> Tuple[str, str, str, int, int, str, str]:
    """Extract constructor signature, documentation, attributes, and byte offsets from a constructor node."""
    signature = ''
    attributes = ''
    signature_content = ''
    signature_end = node.start_byte
    attr_start = node.start_byte
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
            signature_content += attr_text + '\n'
            attr_start = min(attr_start, child.start_byte)
        elif child.type in ('modifier', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
        elif child.type == 'parameter_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
            signature_end = child.end_byte
    normalized_sig = normalize_signature(signature, 'constructor_declaration')
    doc = ''
    doc_start = attr_start
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
            doc_start = min(doc_start, prev_sibling.start_byte)
        prev_sibling = prev_sibling.prev_sibling
    start_byte = doc_start
    logger.debug(f"Detected source signature: {normalized_sig} (type: constructor_declaration)")
    return (normalized_sig, doc.strip(), attributes.strip(), start_byte, signature_end, 'constructor_declaration', signature_content.strip())

def extract_class_info(node: Node, source_text: str) -> Tuple[str, str, str, int, int, str, str]:
    """Extract class signature, documentation, attributes, byte offsets, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    signature_end = node.start_byte
    signature_start = node.start_byte
    identifier_end = node.start_byte
    attr_start = node.start_byte
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
            signature_content += attr_text + '\n'
            attr_start = min(attr_start, child.start_byte)
        elif child.type in ('modifier', 'class', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
            if child.type == 'modifier' and signature_start == node.start_byte:
                signature_start = child.start_byte
            elif child.type == 'class' and signature_start == node.start_byte:
                signature_start = child.start_byte
            if child.type == 'identifier':
                identifier_end = child.end_byte
        elif child.type == 'base_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
            identifier_end = child.end_byte
        elif child.type == 'declaration_list':
            signature = signature.strip()
            signature_content = signature_content.strip()
            signature_end = identifier_end
            break
    normalized_sig = normalize_signature(signature, 'class_declaration')
    doc = ''
    doc_start = attr_start
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
            doc_start = min(doc_start, prev_sibling.start_byte)
        prev_sibling = prev_sibling.prev_sibling
    start_byte = doc_start
    logger.debug(f"Detected source signature: {normalized_sig} (type: class_declaration)")
    return (normalized_sig, doc.strip(), attributes.strip(), start_byte, signature_end, 'class_declaration', signature_content.strip())

def extract_enum_info(node: Node, source_text: str) -> Tuple[str, str, str, int, int, str, str]:
    """Extract enum signature, documentation, attributes, byte offsets, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    signature_end = node.start_byte
    signature_start = node.start_byte
    identifier_end = node.start_byte
    attr_start = node.start_byte
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
            signature_content += attr_text + '\n'
            attr_start = min(attr_start, child.start_byte)
        elif child.type in ('modifier', 'enum', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
            if child.type == 'modifier' and signature_start == node.start_byte:
                signature_start = child.start_byte
            elif child.type == 'enum' and signature_start == node.start_byte:
                signature_start = child.start_byte
            if child.type == 'identifier':
                identifier_end = child.end_byte
        elif child.type == 'base_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
            identifier_end = child.end_byte
        elif child.type == 'enum_member_declaration_list':
            signature = signature.strip()
            signature_content = signature_content.strip()
            signature_end = identifier_end
            break
    normalized_sig = normalize_signature(signature, 'enum_declaration')
    doc = ''
    doc_start = attr_start
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
            doc_start = min(doc_start, prev_sibling.start_byte)
        prev_sibling = prev_sibling.prev_sibling
    start_byte = doc_start
    logger.debug(f"Detected source signature: {normalized_sig} (type: enum_declaration)")
    return (normalized_sig, doc.strip(), attributes.strip(), start_byte, signature_end, 'enum_declaration', signature_content.strip())

def extract_struct_info(node: Node, source_text: str) -> Tuple[str, str, str, int, int, str, str]:
    """Extract struct signature, documentation, attributes, byte offsets, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    signature_end = node.start_byte
    signature_start = node.start_byte
    identifier_end = node.start_byte
    attr_start = node.start_byte
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
            signature_content += attr_text + '\n'
            attr_start = min(attr_start, child.start_byte)
        elif child.type in ('modifier', 'struct', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
            if child.type == 'modifier' and signature_start == node.start_byte:
                signature_start = child.start_byte
            elif child.type == 'struct' and signature_start == node.start_byte:
                signature_start = child.start_byte
            if child.type == 'identifier':
                identifier_end = child.end_byte
        elif child.type == 'base_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
            identifier_end = child.end_byte
        elif child.type == 'declaration_list':
            signature = signature.strip()
            signature_content = signature_content.strip()
            signature_end = identifier_end
            break
    normalized_sig = normalize_signature(signature, 'struct_declaration')
    doc = ''
    doc_start = attr_start
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
            doc_start = min(doc_start, prev_sibling.start_byte)
        prev_sibling = prev_sibling.prev_sibling
    start_byte = doc_start
    logger.debug(f"Detected source signature: {normalized_sig} (type: struct_declaration)")
    return (normalized_sig, doc.strip(), attributes.strip(), start_byte, signature_end, 'struct_declaration', signature_content.strip())

def extract_interface_info(node: Node, source_text: str) -> Tuple[str, str, str, int, int, str, str]:
    """Extract interface signature, documentation, attributes, byte offsets, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    signature_end = node.start_byte
    signature_start = node.start_byte
    identifier_end = node.start_byte
    attr_start = node.start_byte
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
            signature_content += attr_text + '\n'
            attr_start = min(attr_start, child.start_byte)
        elif child.type in ('modifier', 'interface', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
            if child.type == 'modifier' and signature_start == node.start_byte:
                signature_start = child.start_byte
            elif child.type == 'interface' and signature_start == node.start_byte:
                signature_start = child.start_byte
            if child.type == 'identifier':
                identifier_end = child.end_byte
        elif child.type == 'base_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
            identifier_end = child.end_byte
        elif child.type == 'declaration_list':
            signature = signature.strip()
            signature_content = signature_content.strip()
            signature_end = identifier_end
            break
    normalized_sig = normalize_signature(signature, 'interface_declaration')
    doc = ''
    doc_start = attr_start
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
            doc_start = min(doc_start, prev_sibling.start_byte)
        prev_sibling = prev_sibling.prev_sibling
    start_byte = doc_start
    logger.debug(f"Detected source signature: {normalized_sig} (type: interface_declaration)")
    return (normalized_sig, doc.strip(), attributes.strip(), start_byte, signature_end, 'interface_declaration', signature_content.strip())

def parse_source_file(source_content: str) -> List[Tuple[str, str, str, int, int, str, str]]:
    """Parse the source file to extract declarations and their documentation."""
    tree = PARSER.parse(source_content.encode('utf-8'))
    declarations = []

    def traverse_node(node: Node):
        if node.type == 'method_declaration':
            declarations.append(extract_method_info(node, source_content))
        elif node.type == 'constructor_declaration':
            declarations.append(extract_constructor_info(node, source_content))
        elif node.type == 'class_declaration':
            declarations.append(extract_class_info(node, source_content))
        elif node.type == 'enum_declaration':
            declarations.append(extract_enum_info(node, source_content))
        elif node.type == 'struct_declaration':
            declarations.append(extract_struct_info(node, source_content))
        elif node.type == 'interface_declaration':
            declarations.append(extract_interface_info(node, source_content))
        for child in node.children:
            traverse_node(child)

    traverse_node(tree.root_node)
    if not declarations:
        logger.warning("No declarations found in source file.")
    return declarations

def replace_documentation(source_content: str, doc_dict: Dict[str, Tuple[str, str, str]]) -> str:
    """Replace or insert documentation in the source content based on doc_dict."""
    declarations = parse_source_file(source_content)
    declarations.sort(key=lambda x: x[3], reverse=True)
    result = source_content
    for declaration in declarations:
        norm_sig, old_doc, attr, start_byte, end_byte, node_type, signature_content = declaration
        if norm_sig in doc_dict:
            _, new_doc, new_attr = doc_dict[norm_sig]
            new_text = f"{new_doc}\n{signature_content}"
            new_text = new_text.strip()
            result = result[:start_byte] + new_text + result[end_byte:]
            logger.debug(f"Updated documentation for signature: {norm_sig} (type: {node_type})")
            doc_dict.pop(norm_sig)
        else:
            logger.debug(f"No documentation for signature: {norm_sig} (type: {node_type})")
    if doc_dict:
        logger.debug(f"Unmatched doc signatures: {list(doc_dict.keys())}")
    return result

def read_file(file_path: str) -> str:
    """Read the content of a file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            return f.read()
    except UnicodeDecodeError:
        logger.error(f"Failed to read {file_path} with UTF-8 encoding")
        raise

def main():
    """Main function to process C# source and documentation files."""
    parser = argparse.ArgumentParser(description="Replace XML documentation in a C# source file.")
    parser.add_argument('source_file', help="Path to the C# source file")
    parser.add_argument('--doc_file', help="Path to the file containing new XML documentation (defaults to source_file)")
    parser.add_argument('--output_file', help="Path to the output file with updated documentation (defaults to output.txt)")
    args = parser.parse_args()

    print(f"Processing source file: {args.source_file}")
    try:
        source_content = read_file(args.source_file)
    except Exception as e:
        print(f"Error reading source file: {e}")
        logger.error(f"Source file error: {e}")
        sys.exit(1)

    doc_file = args.doc_file or "replaceDocs_doc_file.txt"
    print(f"Using documentation file: {doc_file}")
    try:
        doc_content = source_content if doc_file == args.source_file else read_file(doc_file)
        doc_dict = parse_doc_file(doc_content)
        if not doc_dict and doc_file != args.source_file:
            print("Warning: No documentation parsed from doc_file. No changes will be applied.")
            logger.debug("doc_dict is empty.")
            return
    except Exception as e:
        print(f"Error reading documentation file: {e}")
        logger.error(f"Documentation file error: {e}")
        sys.exit(1)

    updated_content = replace_documentation(source_content, doc_dict)

    output_file = args.output_file or 'replaceDocs_output.txt'
    output_path = os.path.abspath(output_file)
    try:
        with open(output_file, 'w', encoding='utf-8') as f:
            f.write(updated_content)
        print(f"Processing complete. Output written to: {output_path}")
    except Exception as e:
        print(f"Error writing output file {output_file}: {e}")
        logger.error(f"Error writing output: {e}")
        sys.exit(1)

if __name__ == '__main__':
    main()