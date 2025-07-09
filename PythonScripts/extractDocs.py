import argparse
import sys
import logging
import re
from typing import List, Tuple
from tree_sitter import Language, Parser, Node
import tree_sitter_c_sharp

# Set up logging
logging.basicConfig(level=logging.INFO, handlers=[logging.StreamHandler()])
logger = logging.getLogger(__name__)

# Initialize tree-sitter parser for C#
PARSER = Parser()
PARSER.language = Language(tree_sitter_c_sharp.language())

def normalize_signature(signature: str, node_type: str) -> str:
    """Normalize a signature to a single line while preserving type constraints."""
    logger.debug(f"Raw signature before normalization (type: {node_type}): {signature}")
    # Replace newlines and multiple spaces with a single space
    signature = re.sub(r'\s+', ' ', signature).strip()
    # Split signature to handle constraints separately
    parts = signature.split('where')
    main_signature = parts[0].strip()
    constraints = ' where ' + ' where '.join(p.strip() for p in parts[1:]) if len(parts) > 1 else ''
    return main_signature + constraints

def extract_method_info(node: Node, source_text: str) -> Tuple[str, str, str, str]:
    """Extract method signature, documentation, attributes, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
        elif child.type in ('modifier', 'void_type', 'predefined_type', 'identifier', 'generic_name', 'type_parameter_list'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
        elif child.type == 'parameter_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
        elif child.type == 'type_parameter_constraints_clause':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
    signature = signature.strip()
    signature_content = signature_content.strip()
    normalized_sig = normalize_signature(signature, 'method_declaration')
    doc = ''
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
        prev_sibling = prev_sibling.prev_sibling
    logger.debug(f"Extracted method: {normalized_sig}")
    return (normalized_sig, doc.strip(), attributes.strip(), signature_content.strip())

def extract_constructor_info(node: Node, source_text: str) -> Tuple[str, str, str, str]:
    """Extract constructor signature, documentation, attributes, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
        elif child.type in ('modifier', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
        elif child.type == 'parameter_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
    normalized_sig = normalize_signature(signature, 'constructor_declaration')
    doc = ''
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
        prev_sibling = prev_sibling.prev_sibling
    logger.debug(f"Extracted constructor: {normalized_sig}")
    return (normalized_sig, doc.strip(), attributes.strip(), signature_content.strip())

def extract_class_info(node: Node, source_text: str) -> Tuple[str, str, str, str, List[Tuple[str, str, str, str, List]]]:
    """Extract class signature, documentation, attributes, signature content, and its members."""
    signature = ''
    attributes = ''
    signature_content = ''
    members = []
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
        elif child.type in ('modifier', 'class', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
        elif child.type == 'base_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
        elif child.type == 'declaration_list':
            # Process child nodes for methods, constructors, and nested classes/structs
            for decl in child.children:
                if decl.type == 'method_declaration':
                    members.append(extract_method_info(decl, source_text) + ([],))
                elif decl.type == 'constructor_declaration':
                    members.append(extract_constructor_info(decl, source_text) + ([],))
                elif decl.type == 'class_declaration':
                    members.append(extract_class_info(decl, source_text))
                elif decl.type == 'struct_declaration':
                    members.append(extract_struct_info(decl, source_text))
            signature = signature.strip()
            signature_content = signature_content.strip()
            break
    normalized_sig = normalize_signature(signature, 'class_declaration')
    doc = ''
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
        prev_sibling = prev_sibling.prev_sibling
    logger.debug(f"Extracted class: {normalized_sig}")
    return (normalized_sig, doc.strip(), attributes.strip(), signature_content.strip(), members)

def extract_struct_info(node: Node, source_text: str) -> Tuple[str, str, str, str, List[Tuple[str, str, str, str, List]]]:
    """Extract struct signature, documentation, attributes, signature content, and its members."""
    signature = ''
    attributes = ''
    signature_content = ''
    members = []
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
        elif child.type in ('modifier', 'struct', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
        elif child.type == 'base_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
        elif child.type == 'declaration_list':
            # Process child nodes for methods, constructors, and nested classes/structs
            for decl in child.children:
                if decl.type == 'method_declaration':
                    members.append(extract_method_info(decl, source_text) + ([],))
                elif decl.type == 'constructor_declaration':
                    members.append(extract_constructor_info(decl, source_text) + ([],))
                elif decl.type == 'class_declaration':
                    members.append(extract_class_info(decl, source_text))
                elif decl.type == 'struct_declaration':
                    members.append(extract_struct_info(decl, source_text))
            signature = signature.strip()
            signature_content = signature_content.strip()
            break
    normalized_sig = normalize_signature(signature, 'struct_declaration')
    doc = ''
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
        prev_sibling = prev_sibling.prev_sibling
    logger.debug(f"Extracted struct: {normalized_sig}")
    return (normalized_sig, doc.strip(), attributes.strip(), signature_content.strip(), members)

def extract_enum_info(node: Node, source_text: str) -> Tuple[str, str, str, str, List]:
    """Extract enum signature, documentation, attributes, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
        elif child.type in ('modifier', 'enum', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
        elif child.type == 'base_list':
            signature += source_text[child.start_byte:child.end_byte]
            signature_content += source_text[child.start_byte:child.end_byte]
        elif child.type == 'enum_member_declaration_list':
            signature = signature.strip()
            signature_content = signature_content.strip()
            break
    normalized_sig = normalize_signature(signature, 'enum_declaration')
    doc = ''
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
        prev_sibling = prev_sibling.prev_sibling
    logger.debug(f"Extracted enum: {normalized_sig}")
    return (normalized_sig, doc.strip(), attributes.strip(), signature_content.strip(), [])

def extract_interface_info(node: Node, source_text: str) -> Tuple[str, str, str, str, List]:
    """Extract interface signature, documentation, attributes, and signature content."""
    signature = ''
    attributes = ''
    signature_content = ''
    for child in node.children:
        if child.type == 'attribute_list':
            attr_text = source_text[child.start_byte:child.end_byte].strip()
            attributes += attr_text + '\n'
        elif child.type in ('modifier', 'interface', 'identifier'):
            signature += source_text[child.start_byte:child.end_byte] + ' '
            signature_content += source_text[child.start_byte:child.end_byte] + ' '
    normalized_sig = normalize_signature(signature, 'interface_declaration')
    doc = ''
    prev_sibling = node.prev_sibling
    while prev_sibling and prev_sibling.type == 'comment':
        comment_text = source_text[prev_sibling.start_byte:prev_sibling.end_byte].strip()
        if comment_text.startswith('///'):
            doc = comment_text + '\n' + doc
        prev_sibling = prev_sibling.prev_sibling
    logger.debug(f"Extracted interface: {normalized_sig}")
    return (normalized_sig, doc.strip(), attributes.strip(), signature_content.strip(), [])

def parse_source_file(source_content: str) -> List[Tuple[str, str, str, str, List[Tuple[str, str, str, str, List]]]]:
    """Parse the source file to extract top-level declarations, documentation, and their members."""
    tree = PARSER.parse(source_content.encode('utf-8'))
    declarations = []

    def traverse_node(node: Node, parent_type: str = ""):
        # Only process top-level declarations (children of compilation_unit)
        if parent_type == 'compilation_unit':
            if node.type == 'class_declaration':
                declarations.append(extract_class_info(node, source_content))
            elif node.type == 'enum_declaration':
                declarations.append(extract_enum_info(node, source_content))
            elif node.type == 'interface_declaration':
                declarations.append(extract_interface_info(node, source_content))
            # Structs are only processed if they are top-level (not nested in classes)
            elif node.type == 'struct_declaration' and parent_type == 'compilation_unit':
                declarations.append(extract_struct_info(node, source_content))
        # Recursively process child nodes
        for child in node.children:
            traverse_node(child, node.type if node.type in ('compilation_unit', 'class_declaration', 'struct_declaration') else parent_type)

    traverse_node(tree.root_node, 'compilation_unit')
    if not declarations:
        logger.warning("No declarations found in source file.")
    return declarations

def format_output(declarations: List[Tuple[str, str, str, str, List[Tuple[str, str, str, str, List]]]]) -> str:
    """Format the output to preserve class/struct structure with documentation and attributes."""
    output_lines = []
    indent = "    "

    def format_declaration(doc, attr, sig_content, members, level=0):
        current_indent = indent * level
        if doc:
            output_lines.extend([current_indent + line for line in doc.split('\n')])
        if attr:
            output_lines.extend([current_indent + line for line in attr.split('\n')])
        output_lines.append(current_indent + sig_content)
        # Only add braces if there are members or if it's a class (to preserve structure)
        if members or 'class ' in sig_content:
            output_lines.append(current_indent + "{")
            for _, member_doc, member_attr, member_sig, member_members in members:
                format_declaration(member_doc, member_attr, member_sig, member_members, level + 1)
            output_lines.append(current_indent + "}")
        output_lines.append("")

    for _, doc, attr, sig_content, members in declarations:
        format_declaration(doc, attr, sig_content, members)

    return "\n".join(output_lines).rstrip()

def read_file(file_path: str) -> str:
    """Read the content of a file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            return f.read()
    except UnicodeDecodeError:
        logger.error(f"Failed to read {file_path} with UTF-8 encoding")
        raise
    except Exception as e:
        logger.error(f"Error reading file {file_path}: {e}")
        raise

def main():
    """Main function to parse C# source file and output documentation, attributes, and signatures."""
    parser = argparse.ArgumentParser(description="Parse C# source file and extract documentation, attributes, and signatures.")
    parser.add_argument('source_file', help="Path to the C# source file")
    parser.add_argument('--output_file', help="Path to the output file (defaults to output.txt)")
    args = parser.parse_args()

    print(f"Processing source file: {args.source_file}")
    try:
        source_content = read_file(args.source_file)
    except Exception as e:
        print(f"Error reading source file: {e}")
        sys.exit(1)

    declarations = parse_source_file(source_content)
    output_content = format_output(declarations)
    output_file = args.output_file or 'extractDocs_output.txt'
    try:
        with open(output_file, 'w', encoding='utf-8') as f:
            f.write(output_content)
        print(f"Output written to: {output_file}")
    except Exception as e:
        print(f"Error writing output file {output_file}: {e}")
        sys.exit(1)

if __name__ == '__main__':
    main()